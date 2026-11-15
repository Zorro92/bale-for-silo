//! Go/no-go spike for porting the HTTP streams (docs/rust-port.md): download the server's
//! bandwidth probe from inside the app with a Rust HTTP + TLS stack and report throughput and this
//! thread's CPU time, to compare with the C# SocketsHttpHandler path (`bale-debug.sh speed test`).
//! Debug-only. The URL and headers carry credentials: nothing here logs or echoes them, and errors
//! are fixed category names.

use std::io::Read;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
pub struct ProbeArgs {
    url: String,
    headers: Vec<(String, String)>,
    /// Stop this long after the first body byte.
    limit_ms: u64,
}

#[derive(Serialize, Debug, Default)]
pub struct ProbeReport {
    status: u16,
    error: Option<&'static str>,
    /// Request start to response headers.
    headers_ms: u64,
    /// After the first body byte: bytes and time, and the rate over the last 3 s (ramp excluded).
    bytes: u64,
    ms: u64,
    mbps: f64,
    last_3s_mbps: f64,
    /// CPU time of the downloading thread, user + kernel (Windows only).
    cpu_ms: Option<f64>,
}

pub fn probe(args: &ProbeArgs) -> ProbeReport {
    let mut report = ProbeReport::default();
    let cpu_start = thread_cpu_ms();
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .timeout_connect(Some(Duration::from_secs(10)))
        .http_status_as_error(false)
        .build()
        .into();
    let mut request = agent.get(&args.url);
    for (name, value) in &args.headers {
        request = request.header(name, value);
    }
    let started = Instant::now();
    let response = match request.call() {
        Ok(response) => response,
        Err(error) => {
            report.error = Some(category(&error));
            return report;
        }
    };
    report.headers_ms = started.elapsed().as_millis() as u64;
    report.status = response.status().as_u16();
    if !response.status().is_success() {
        report.error = Some("status");
        return report;
    }
    let mut body = response
        .into_body()
        .into_with_config()
        .limit(u64::MAX)
        .reader();
    let mut buffer = vec![0u8; 64 * 1024];
    let mut first: Option<Instant> = None;
    let mut samples: Vec<(u64, u64)> = Vec::new(); // (elapsed ms, cumulative bytes)
    loop {
        match body.read(&mut buffer) {
            Ok(0) => break,
            Ok(count) => {
                let first = *first.get_or_insert_with(Instant::now);
                report.bytes += count as u64;
                let elapsed = first.elapsed().as_millis() as u64;
                samples.push((elapsed, report.bytes));
                if elapsed >= args.limit_ms {
                    break;
                }
            }
            Err(_) => {
                report.error = Some("read");
                break;
            }
        }
    }
    if let Some(first) = first {
        report.ms = first.elapsed().as_millis() as u64;
        if report.ms > 0 {
            report.mbps = report.bytes as f64 * 8.0 / report.ms as f64 / 1000.0;
        }
        let end = samples.last().copied().unwrap_or_default();
        if let Some(start) = samples.iter().rev().find(|(ms, _)| end.0 - ms >= 3000) {
            report.last_3s_mbps =
                (end.1 - start.1) as f64 * 8.0 / (end.0 - start.0) as f64 / 1000.0;
        }
    }
    report.cpu_ms = thread_cpu_ms()
        .zip(cpu_start)
        .map(|(end, start)| end - start);
    report
}

fn category(error: &ureq::Error) -> &'static str {
    match error {
        ureq::Error::Timeout(_) => "timeout",
        ureq::Error::HostNotFound => "dns",
        ureq::Error::ConnectionFailed => "connect",
        ureq::Error::Io(_) => "io",
        ureq::Error::Tls(_) | ureq::Error::Rustls(_) => "tls",
        _ => "other",
    }
}

#[cfg(windows)]
fn thread_cpu_ms() -> Option<f64> {
    unsafe extern "system" {
        fn GetCurrentThread() -> isize;
        fn GetThreadTimes(
            thread: isize,
            creation: *mut u64,
            exit: *mut u64,
            kernel: *mut u64,
            user: *mut u64,
        ) -> i32;
    }
    let (mut creation, mut exit, mut kernel, mut user) = (0u64, 0u64, 0u64, 0u64);
    // SAFETY: the pseudo-handle is always valid; the out-pointers are live locals (FILETIME is 8 bytes).
    let ok = unsafe {
        GetThreadTimes(
            GetCurrentThread(),
            &mut creation,
            &mut exit,
            &mut kernel,
            &mut user,
        )
    };
    (ok != 0).then(|| (kernel + user) as f64 / 10_000.0)
}

#[cfg(not(windows))]
fn thread_cpu_ms() -> Option<f64> {
    None
}

#[cfg(test)]
mod tests {
    /// Manual: `cargo test --release -- --ignored probe_public`; needs the internet.
    #[test]
    #[ignore]
    fn probe_public() {
        let args = super::ProbeArgs {
            url: "https://proof.ovh.net/files/100Mb.dat".to_owned(),
            headers: vec![("Accept".to_owned(), "*/*".to_owned())],
            limit_ms: 5000,
        };
        let report = super::probe(&args);
        println!("{report:?}");
        assert!(report.error.is_none() && report.bytes > 0);
    }
}
