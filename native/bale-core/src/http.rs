//! The shared HTTP client for Rust's media transport: one ureq agent (connection pool) for the
//! app's lifetime, with a caching resolver. Used by the range stream and the debug benchmarks.

use std::time::{Duration, Instant};

/// The app-wide agent: warm connections are reused across streams, as the C# HttpClient's are.
pub fn agent() -> &'static ureq::Agent {
    static AGENT: std::sync::OnceLock<ureq::Agent> = std::sync::OnceLock::new();
    AGENT.get_or_init(|| {
        let config = ureq::Agent::config_builder()
            .timeout_connect(Some(Duration::from_secs(10)))
            .timeout_global(Some(Duration::from_secs(30)))
            .http_status_as_error(false)
            .max_idle_connections_per_host(8)
            // SocketsHttpHandler's PooledConnectionIdleTimeout; ureq's default is 15 s, which let a
            // spare connection expire between seeks.
            .max_idle_age(Duration::from_secs(60))
            .build();
        ureq::Agent::with_parts(
            config,
            ureq::unversioned::transport::DefaultConnector::new(),
            CachingResolver::default(),
        )
    })
}

/// ureq resolves the host before it looks in its connection pool, so every request paid a DNS
/// lookup (on a spawned thread, with a timeout set) even on a warm connection; the C# handler takes a
/// pooled connection without one. Resolve each host once a minute instead.
#[derive(Debug, Default)]
struct CachingResolver {
    inner: ureq::unversioned::resolver::DefaultResolver,
    cache:
        std::sync::Mutex<std::collections::HashMap<String, (Instant, Vec<std::net::SocketAddr>)>>,
}

impl ureq::unversioned::resolver::Resolver for CachingResolver {
    fn resolve(
        &self,
        uri: &ureq::http::Uri,
        config: &ureq::config::Config,
        timeout: ureq::unversioned::transport::NextTimeout,
    ) -> Result<ureq::unversioned::resolver::ResolvedSocketAddrs, ureq::Error> {
        const FRESH: Duration = Duration::from_secs(60);
        let key = format!(
            "{}://{}",
            uri.scheme_str().unwrap_or_default(),
            uri.authority().map_or("", |a| a.as_str())
        );
        let cache = self
            .cache
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some((at, addresses)) = cache.get(&key)
            && at.elapsed() < FRESH
        {
            let mut resolved = self.empty();
            for address in addresses {
                resolved.push(*address);
            }
            return Ok(resolved);
        }
        drop(cache);
        let resolved = self.inner.resolve(uri, config, timeout)?;
        let addresses = resolved.iter().copied().collect();
        self.cache
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(key, (Instant::now(), addresses));
        Ok(resolved)
    }
}

/// Opens a pooled connection to `url`'s host in the background: a small GET whose response is read
/// to its end, so the connection returns to the pool. A progressive remux's connection is dropped
/// at every seek (its body is unfinished), and the C# client's pool, shared with the API, still had
/// a warm one; without this the next Rust open paid TCP and TLS handshakes (~0.4 s at 150 ms RTT).
/// Failures are ignored; nothing is logged.
pub fn warm(url: String) {
    let _ = std::thread::Builder::new()
        .name("bale-http-warm".into())
        .spawn(move || {
            if let Ok(response) = agent()
                .get(&url)
                .config()
                .timeout_global(Some(Duration::from_secs(10)))
                .build()
                .call()
            {
                let mut body = response
                    .into_body()
                    .into_with_config()
                    .limit(64 * 1024)
                    .reader();
                let _ = std::io::copy(&mut body, &mut std::io::sink());
            }
        });
}

/// Kiota's `RetryHandler` defaults, which every C# media request goes through: 429, 503 and 504 are
/// retried up to three times, after the response's Retry-After (seconds or an HTTP date) or else
/// 3 s doubling per attempt (3, 6, 12 s), never more than 180 s. Measured against the C# stream
/// over a loopback server (docs/rust-port.md, slice 7 step 6).
pub const MAX_RETRIES: u32 = 3;
const RETRY_BASE: Duration = Duration::from_secs(3);
const RETRY_CAP: Duration = Duration::from_secs(180);

/// The wait before retry `attempt` (1-based) of a response with `status`, or None when it is not
/// retried. `now` is the Unix time in seconds, for a Retry-After date.
pub fn retry_delay(
    status: u16,
    retry_after: Option<&str>,
    attempt: u32,
    now: u64,
) -> Option<Duration> {
    if !matches!(status, 429 | 503 | 504) || attempt == 0 || attempt > MAX_RETRIES {
        return None;
    }
    let after = retry_after.map(str::trim).and_then(|value| {
        value
            .parse::<u64>()
            .ok()
            .map(Duration::from_secs)
            .or_else(|| http_date(value).map(|at| Duration::from_secs(at.saturating_sub(now))))
    });
    let delay = after.unwrap_or_else(|| RETRY_BASE * 2u32.pow(attempt - 1));
    Some(delay.min(RETRY_CAP))
}

/// An IMF-fixdate (`Sun, 06 Nov 1994 08:49:37 GMT`) as Unix seconds.
pub fn http_date(value: &str) -> Option<u64> {
    let mut parts = value.split_whitespace();
    let (_weekday, day, month, year, time, zone) = (
        parts.next()?,
        parts.next()?.parse::<u64>().ok()?,
        parts.next()?,
        parts.next()?.parse::<i64>().ok()?,
        parts.next()?,
        parts.next()?,
    );
    if zone != "GMT" || parts.next().is_some() {
        return None;
    }
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    let month = MONTHS.iter().position(|m| *m == month)? as i64 + 1;
    let mut clock = time.split(':').map(|part| part.parse::<u64>().ok());
    let (hour, minute, second) = (clock.next()??, clock.next()??, clock.next()??);
    if !(1..=31).contains(&day) || hour > 23 || minute > 59 || second > 60 || year < 1970 {
        return None;
    }
    // Days from the civil date (Howard Hinnant's algorithm).
    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (month + 9) % 12;
    let doy = (153 * mp + 2) / 5 + day as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146_097 + doe - 719_468;
    Some(days as u64 * 86_400 + hour * 3600 + minute * 60 + second)
}

pub fn unix_now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |since| since.as_secs())
}

/// How a retried request ended.
pub enum Retried {
    Response(ureq::http::Response<ureq::Body>),
    /// 429/503/504 after every retry: the C# handler fails the request, and the stream's own demand
    /// retry does not run.
    Exhausted,
    /// `wait` reported the stream closed.
    Stopped,
}

/// Sends a GET through `call` (which adds `Retry-Attempt: n` when given an attempt number) and
/// retries 429/503/504 per `retry_delay`. `wait` sleeps for a delay and returns false once closed.
/// Transport errors pass through unretried, as in the C# handler.
pub fn call_with_retry<E>(
    mut call: impl FnMut(Option<u32>) -> Result<ureq::http::Response<ureq::Body>, E>,
    mut wait: impl FnMut(Duration) -> bool,
) -> Result<Retried, E> {
    let mut attempt = 0;
    loop {
        let response = call((attempt > 0).then_some(attempt))?;
        let status = response.status().as_u16();
        if !matches!(status, 429 | 503 | 504) {
            return Ok(Retried::Response(response));
        }
        attempt += 1;
        let retry_after = response
            .headers()
            .get("retry-after")
            .and_then(|value| value.to_str().ok())
            .map(str::to_owned);
        drop(response);
        let Some(delay) = retry_delay(status, retry_after.as_deref(), attempt, unix_now()) else {
            return Ok(Retried::Exhausted);
        };
        if !wait(delay) {
            return Ok(Retried::Stopped);
        }
    }
}

#[cfg(test)]
mod retry_tests {
    use super::*;

    #[test]
    fn delays_follow_kiota() {
        let s = |n| Some(Duration::from_secs(n));
        assert_eq!(retry_delay(503, None, 1, 0), s(3));
        assert_eq!(retry_delay(504, None, 2, 0), s(6));
        assert_eq!(retry_delay(429, None, 3, 0), s(12));
        assert_eq!(retry_delay(429, None, 4, 0), None);
        assert_eq!(retry_delay(500, None, 1, 0), None);
        assert_eq!(retry_delay(429, Some("1"), 1, 0), s(1));
        assert_eq!(retry_delay(429, Some(" 900 "), 1, 0), s(180));
        assert_eq!(retry_delay(503, Some("soon"), 2, 0), s(6));
        let at = http_date("Sun, 06 Nov 1994 08:49:37 GMT").unwrap();
        assert_eq!(at, 784_111_777);
        assert_eq!(
            retry_delay(503, Some("Sun, 06 Nov 1994 08:49:42 GMT"), 1, at),
            s(5)
        );
        assert_eq!(
            retry_delay(503, Some("Sun, 06 Nov 1994 08:49:30 GMT"), 1, at),
            s(0)
        );
        assert_eq!(http_date("Sun, 06 Nov 1994 08:49:37 PST"), None);
        assert_eq!(
            http_date("Tue, 29 Feb 2028 00:00:00 GMT"),
            Some(1_835_395_200)
        );
    }
}
