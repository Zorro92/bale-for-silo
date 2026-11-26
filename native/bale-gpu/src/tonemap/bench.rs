//! Debug-package stage bench (Cargo feature stage-timing, 2026-11-26): the stage's GPU time per apply
//! with each NV12 dither, so the console can compare Bayer against rav1g's R2 (docs/gpu-tone-map.md
//! "Bayer against R2 on the console"). Its console run (2026-11-26) found no cost, and R2 became the
//! default; the bench still compares the two, Bayer through debug `bayerdither`, with the same report.
//! In playback the stage's event-query total is an upper bound that queueing dominates (19 to 52 ms per
//! frame on the console, 2026-11-24, against a stage of a few ms), while a dither costs about 0.03 to
//! 0.1 ms.
//!
//! So this runs the stage alone on a private hardware D3D11 device and times each apply with GPU
//! timestamps, as the grain probe times its shader (`grain::probe`, console results 2026-11-08).
//! Timestamps crashed the console's driver only on Media Foundation's device, whose video command
//! lists they also covered (timing_gpu.rs); this device does no video work. Per case a Bayer
//! (`bayerdither`) and an R2 (default) stage, both with the other BALE_TONEMAP_DEBUG words as set,
//! apply the same frame in turn, to NV12 and then to P010: P010 never dithers, so its R2 - Bayer
//! difference is the measurement's own noise. The input is shader-readable, so apply reads it in
//! place and the time is the stage's passes only (no input copy). Each apply is flushed and waited
//! for: the times are of a stage that has the GPU to itself. Never during playback (the app refuses
//! it there); stage timing must be off (it would add its event queries and submissions to the
//! bench's stages).
use super::{cpu, gpu::ToneMapGpu, types::*};
use crate::grain::gpu::{texture, texture_desc};
use std::fmt::Write as _;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{E_FAIL, HMODULE, S_FALSE, S_OK};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL,
};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::{DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_WAIT_TIMEOUT};
use windows::core::{Interface, Result};

/// Timed applies per arm when the caller passes 0, and the most one call takes.
pub const DEFAULT_FRAMES: u32 = 60;
pub const MAX_FRAMES: u32 = 240;
const WARMUP_FRAMES: usize = 5;
/// A timestamp pair still pending after this is a failure, never a wait without end.
const QUERY_TIMEOUT: Duration = Duration::from_secs(2);

/// One stage configuration: the visible source, the output (display) size and the frames' colour.
#[derive(Clone, Copy)]
struct Case {
    name: &'static str,
    source: (u32, u32),
    output: (u32, u32),
    pq: bool,
}

/// The routes that write dithered NV12 on an 8-bit SDR display: HDR10 tone-mapped on a 1080p and on a
/// 4K TV, 1080p HDR10 on a 1080p TV, and 4K SDR downscaled for a 1080p TV.
const CASES: [Case; 4] = [
    Case {
        name: "4K PQ -> 1080p (2:1, staged)",
        source: (3840, 2160),
        output: (1920, 1080),
        pq: true,
    },
    Case {
        name: "4K PQ 1:1",
        source: (3840, 2160),
        output: (3840, 2160),
        pq: true,
    },
    Case {
        name: "1080p PQ 1:1",
        source: (1920, 1080),
        output: (1920, 1080),
        pq: true,
    },
    Case {
        name: "4K SDR -> 1080p (2:1, staged)",
        source: (3840, 2160),
        output: (1920, 1080),
        pq: false,
    },
];

/// (stage, output) per arm: stage 0 is Bayer (`bayerdither`), 1 is R2 (the default); output 0 is
/// NV12, 1 is P010. The order and labels are the report's since the first console run (2026-11-26).
const ARMS: [(usize, usize, &str); 4] = [
    (0, 0, "NV12 Bayer"),
    (1, 0, "NV12 R2"),
    (0, 1, "P010 Bayer"),
    (1, 1, "P010 R2"),
];

/// Why the bench does not run, if it does not: an out-of-range frame count, or stage timing on.
fn refusal(frames: u32, timing_on: bool) -> Option<String> {
    if frames > MAX_FRAMES {
        Some(format!(
            "tonemap bench: frames 1-{MAX_FRAMES} (0 = {DEFAULT_FRAMES})\n"
        ))
    } else if timing_on {
        Some(
            "tonemap bench: stage GPU timing is on; turn it off first (gpu-timing off), or its event \
             queries time the bench's stages too\n"
                .into(),
        )
    } else {
        None
    }
}

/// The bench on a fresh hardware device, as the export runs it.
pub(super) fn run(frames: u32) -> String {
    if let Some(text) = refusal(
        frames,
        super::timing::ENABLED.load(std::sync::atomic::Ordering::Relaxed),
    ) {
        return text;
    }
    let frames = if frames == 0 { DEFAULT_FRAMES } else { frames } as usize;
    let words = std::env::var("BALE_TONEMAP_DEBUG").unwrap_or_default();
    let mut report = String::new();
    match BenchDevice::create(D3D_DRIVER_TYPE_HARDWARE) {
        Ok(device) => run_on(&device, &CASES, frames, WARMUP_FRAMES, &words, &mut report),
        Err(error) => {
            let _ = writeln!(report, "tonemap bench: hardware device {}", failure(&error));
        }
    }
    report
}

fn failure(error: &windows::core::Error) -> String {
    if error.code() == DXGI_ERROR_WAIT_TIMEOUT {
        format!(
            "FAILS: timestamps pending after {} ms (0x{:08X})",
            QUERY_TIMEOUT.as_millis(),
            error.code().0 as u32
        )
    } else {
        format!("FAILS 0x{:08X}", error.code().0 as u32)
    }
}

struct BenchDevice {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    multithread: ID3D10Multithread,
    level: D3D_FEATURE_LEVEL,
}

impl BenchDevice {
    /// A private device like a player open's (hardware: VIDEO_SUPPORT, as Media Foundation's; the
    /// WARP test takes no flags), multithread protected; its release is counted with the player
    /// devices (`mem`).
    fn create(driver: D3D_DRIVER_TYPE) -> Result<Self> {
        let flags = if driver == D3D_DRIVER_TYPE_HARDWARE {
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT
        } else {
            D3D11_CREATE_DEVICE_FLAG(0)
        };
        let (mut device, mut context, mut level) = (None, None, D3D_FEATURE_LEVEL::default());
        // SAFETY: no adapter or software module; the outputs are live local slots.
        unsafe {
            D3D11CreateDevice(
                None,
                driver,
                HMODULE::default(),
                flags,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                Some(&mut level),
                Some(&mut context),
            )?
        };
        let (device, context) = (device.ok_or(E_FAIL)?, context.ok_or(E_FAIL)?);
        // The release counter is a debug-probes readout; the bench also builds with stage-timing alone.
        #[cfg(feature = "debug-probes")]
        crate::lifecycle::watch_release(&device);
        let multithread: ID3D10Multithread = context.cast()?;
        // SAFETY: configured before any work; the result is the previous setting, not an error.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        Ok(Self {
            device,
            context,
            multithread,
            level,
        })
    }
}

/// The device's multithread lock for the bench's own calls (recursive; apply takes it again).
struct Lock<'a>(&'a ID3D10Multithread);

impl<'a> Lock<'a> {
    fn enter(multithread: &'a ID3D10Multithread) -> Self {
        // SAFETY: live interface; Drop pairs the Leave on this thread, unwinding included.
        unsafe { multithread.Enter() };
        Self(multithread)
    }
}

impl Drop for Lock<'_> {
    fn drop(&mut self) {
        // SAFETY: paired with this guard's Enter.
        unsafe { self.0.Leave() };
    }
}

struct Timestamps {
    disjoint: ID3D11Query,
    start: ID3D11Query,
    end: ID3D11Query,
}

impl Timestamps {
    fn new(device: &ID3D11Device) -> Result<Self> {
        let query = |kind| -> Result<ID3D11Query> {
            let mut query = None;
            // SAFETY: known query kind, no misc flags, live descriptor and local output slot.
            unsafe {
                device.CreateQuery(
                    &D3D11_QUERY_DESC {
                        Query: kind,
                        MiscFlags: 0,
                    },
                    Some(&mut query),
                )?
            };
            query.ok_or_else(|| E_FAIL.into())
        };
        Ok(Self {
            disjoint: query(D3D11_QUERY_TIMESTAMP_DISJOINT)?,
            start: query(D3D11_QUERY_TIMESTAMP)?,
            end: query(D3D11_QUERY_TIMESTAMP)?,
        })
    }

    /// GPU milliseconds of `apply`, flushed and waited for; None when the interval was disjoint.
    fn time(
        &self,
        device: &BenchDevice,
        apply: impl FnOnce() -> Result<()>,
    ) -> Result<Option<f64>> {
        {
            let _lock = Lock::enter(&device.multithread);
            // SAFETY: owned queries on this locked immediate context; disjoint takes Begin/End,
            // timestamps End only.
            unsafe {
                device.context.Begin(&self.disjoint);
                device.context.End(&self.start);
            }
            let result = apply();
            // SAFETY: close the interval even when apply failed, and submit it.
            unsafe {
                device.context.End(&self.end);
                device.context.End(&self.disjoint);
                device.context.Flush();
            }
            result?;
        }
        self.read(device)
    }

    fn read(&self, device: &BenchDevice) -> Result<Option<f64>> {
        let deadline = Instant::now() + QUERY_TIMEOUT;
        let mut disjoint = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        let (mut start, mut end) = (0u64, 0u64);
        loop {
            let ready = {
                let _lock = Lock::enter(&device.multithread);
                // SAFETY: each query was ended and flushed on this context; each output is live,
                // exclusively writable and exactly the size its query kind writes.
                unsafe {
                    ready(
                        &device.context,
                        &self.disjoint,
                        std::ptr::from_mut(&mut disjoint).cast(),
                        size_of_val(&disjoint) as u32,
                    )? && ready(
                        &device.context,
                        &self.start,
                        std::ptr::from_mut(&mut start).cast(),
                        size_of_val(&start) as u32,
                    )? && ready(
                        &device.context,
                        &self.end,
                        std::ptr::from_mut(&mut end).cast(),
                        size_of_val(&end) as u32,
                    )?
                }
            };
            if ready {
                if disjoint.Disjoint.as_bool() {
                    return Ok(None);
                }
                if disjoint.Frequency == 0 || end < start {
                    return Err(E_FAIL.into());
                }
                return Ok(Some(
                    (end - start) as f64 * 1000.0 / disjoint.Frequency as f64,
                ));
            }
            if Instant::now() >= deadline {
                return Err(DXGI_ERROR_WAIT_TIMEOUT.into());
            }
            // Never hold the lock while waiting.
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

/// # Safety
/// `data` is writable for `size` bytes in the layout of the query's kind; the caller holds the
/// context's multithread lock and has ended and flushed the query.
unsafe fn ready(
    context: &ID3D11DeviceContext,
    query: &ID3D11Query,
    data: *mut core::ffi::c_void,
    size: u32,
) -> Result<bool> {
    // SAFETY: as documented; the vtable call keeps S_FALSE (not ready), which the typed wrapper
    // folds into Ok.
    let hr = unsafe {
        (context.vtable().GetData)(
            context.as_raw(),
            query.as_raw(),
            data,
            size,
            D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
        )
    };
    match hr {
        S_OK => Ok(true),
        S_FALSE => Ok(false),
        _ => Err(hr.into()),
    }
}

/// Median, 95th percentile and minimum of `samples` (sorted in place).
fn summary(samples: &mut [f64]) -> Option<(f64, f64, f64)> {
    samples.sort_by(f64::total_cmp);
    let at = |q: f64| samples[((samples.len() - 1) as f64 * q).round() as usize];
    (!samples.is_empty()).then(|| (at(0.5), at(0.95), samples[0]))
}

fn run_on(
    device: &BenchDevice,
    cases: &[Case],
    frames: usize,
    warmup: usize,
    words: &str,
    report: &mut String,
) {
    // The other words apply to both stages; the dither is the bench's: R2 is the default, Bayer takes
    // `bayerdither`.
    let others: Vec<&str> = words
        .split([',', ' '])
        .filter(|w| !w.is_empty() && *w != "bayerdither")
        .collect();
    let r2 = others.join(",");
    let bayer = others
        .iter()
        .copied()
        .chain(["bayerdither"])
        .collect::<Vec<_>>()
        .join(",");
    let _ = writeln!(
        report,
        "tonemap bench: GPU timestamps per apply on a private D3D11 device (feature level 0x{:04X}, no \
         video work), {frames} timed applies per arm after {warmup} warm-up, Bayer and R2 interleaved \
         (NV12, then the P010 control), input read in place; other tonemap-debug words: {}",
        device.level.0,
        if r2.is_empty() { "none" } else { &r2 }
    );
    for case in cases {
        if let Err(error) = bench_case(device, case, frames, warmup, [&bayer, &r2], report) {
            let _ = writeln!(report, "  {}: {}", case.name, failure(&error));
            // SAFETY: metadata query on the live device.
            if unsafe { device.device.GetDeviceRemovedReason() }.is_err()
                || error.code() == DXGI_ERROR_DEVICE_REMOVED
            {
                let _ = writeln!(report, "  device removed: remaining cases skipped");
                break;
            }
        }
    }
}

fn bench_case(
    device: &BenchDevice,
    case: &Case,
    frames: usize,
    warmup: usize,
    words: [&str; 2],
    report: &mut String,
) -> Result<()> {
    let started = Instant::now();
    let ((sw, sh), (w, h)) = (case.source, case.output);
    let config = BaleToneMapConfig {
        width: w,
        height: h,
        curve: 1,
        source_peak_nits: 1000.0,
        target_white_nits: 100.0,
        reserved: if (sw, sh) == (w, h) {
            [0; 4]
        } else {
            [sw, sh, 0, 0]
        },
        ..Default::default()
    };
    let frame = BaleToneMapFrameInfo {
        transfer: if case.pq { 16 } else { 1 },
        primaries: if case.pq { 9 } else { 1 },
        matrix: if case.pq { 9 } else { 1 },
        chroma_location: 1,
        ..Default::default()
    };
    // PQ: the self-test's saturated 2x2 colour blocks (sharp chroma edges); SDR: a full-range ramp.
    let pixels = if case.pq {
        cpu::test_pattern(sw, sh, true, false)
    } else {
        cpu::sdr_ramp(sw, sh, 64.0, 940.0)
    };
    let srv = D3D11_BIND_SHADER_RESOURCE.0 as u32;
    let input = texture(
        crate::memledger::TONE_MAP,
        &device.device,
        &texture_desc(sw, sh, DXGI_FORMAT_P010, srv),
        Some(&D3D11_SUBRESOURCE_DATA {
            pSysMem: pixels.as_ptr().cast(),
            SysMemPitch: sw * 2,
            SysMemSlicePitch: 0,
        }),
    )?;
    drop(pixels);
    let target = (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32;
    let outputs = [
        texture(
            crate::memledger::TONE_MAP,
            &device.device,
            &texture_desc(w, h, DXGI_FORMAT_NV12, target),
            None,
        )?,
        texture(
            crate::memledger::TONE_MAP,
            &device.device,
            &texture_desc(w, h, DXGI_FORMAT_P010, target),
            None,
        )?,
    ];
    let mut stages = {
        // Create holds the device lock, as the fork's decode thread does.
        let _lock = Lock::enter(&device.multithread);
        [
            ToneMapGpu::create_unrecorded(&device.device, config, words[0])?,
            ToneMapGpu::create_unrecorded(&device.device, config, words[1])?,
        ]
    };
    if stages[0].dither() != cpu::Dither::Bayer || stages[1].dither() != cpu::Dither::R2 {
        return Err(E_FAIL.into());
    }
    let timestamps = Timestamps::new(&device.device)?;
    let mut samples: [Vec<f64>; 4] = Default::default();
    let mut disjoint = 0;
    // NV12 first, then the P010 control, each with Bayer and R2 interleaved apply by apply: drift
    // hits both dithers alike, and a stage's constants stay put within a phase (no upload per apply).
    for phase in 0..2 {
        let arms: Vec<usize> = (0..ARMS.len())
            .filter(|&arm| ARMS[arm].1 == phase)
            .collect();
        for _ in 0..warmup {
            for &arm in &arms {
                let (stage, output, _) = ARMS[arm];
                let _lock = Lock::enter(&device.multithread);
                stages[stage].apply(&device.context, &input, 0, &outputs[output], &frame)?;
                // SAFETY: live, locked immediate context; the bench owns its submissions.
                unsafe { device.context.Flush() };
            }
        }
        for _ in 0..frames {
            for &arm in &arms {
                let (stage, output, _) = ARMS[arm];
                let measured = timestamps.time(device, || {
                    stages[stage].apply(&device.context, &input, 0, &outputs[output], &frame)
                })?;
                match measured {
                    Some(ms) => samples[arm].push(ms),
                    None => disjoint += 1,
                }
            }
        }
    }
    let medians: Vec<Option<(f64, f64, f64)>> = samples.iter_mut().map(|s| summary(s)).collect();
    let _ = write!(report, "  {}:", case.name);
    for ((_, _, label), stats) in ARMS.iter().zip(&medians) {
        match stats {
            Some((median, p95, min)) => {
                let _ = write!(
                    report,
                    " {label} {median:.3} ms (p95 {p95:.3}, min {min:.3});"
                );
            }
            None => {
                let _ = write!(report, " {label} unavailable;");
            }
        }
    }
    let delta = |r2: usize, bayer: usize| match (medians[r2], medians[bayer]) {
        (Some((a, _, _)), Some((b, _, _))) => format!("{:+.3} ms", a - b),
        _ => "unavailable".into(),
    };
    let _ = writeln!(
        report,
        "\n    R2 - Bayer (medians): NV12 {}; P010 {} (no dither: the noise); {disjoint} disjoint; {} ms wall",
        delta(1, 0),
        delta(3, 2),
        started.elapsed().as_millis()
    );
    Ok(())
}

/// Runs the stage bench (debug packages, Cargo feature stage-timing): `frames` timed applies per arm
/// (0 = 60, at most 240), blocking the calling thread for a few seconds. Writes a UTF-8 report without
/// a trailing NUL, truncated at a character boundary, and returns its length; a panic writes "panic".
/// Never during playback.
/// # Safety
/// A non-null `report` points to `capacity` exclusively writable bytes for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_debug_bench(
    frames: u32,
    report: *mut u8,
    capacity: u32,
) -> u32 {
    if report.is_null() || capacity == 0 {
        return 0;
    }
    // Every GPU resource unwinds before the fallback text is written.
    let text = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(frames)));
    let text = text.as_deref().unwrap_or("panic");
    // SAFETY: the caller guarantees capacity writable bytes; write_report bounds the copy.
    unsafe { crate::write_report(text, report, capacity) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_WARP;

    #[test]
    fn refuses_out_of_range_frames_and_running_stage_timing() {
        assert_eq!(refusal(0, false), None);
        assert_eq!(refusal(MAX_FRAMES, false), None);
        assert!(
            refusal(MAX_FRAMES + 1, false)
                .unwrap()
                .contains("frames 1-240")
        );
        assert!(refusal(60, true).unwrap().contains("gpu-timing off"));
        let mut samples = [3.0, 1.0, 2.0];
        assert_eq!(summary(&mut samples), Some((2.0, 3.0, 1.0)));
        assert_eq!(summary(&mut []), None);
    }

    /// The bench on WARP at small sizes: every case reports all four arms and both differences, and
    /// keeps the other debug words for both stages (the dither word is the bench's own).
    #[test]
    fn warp_bench_reports_every_arm() {
        let device = BenchDevice::create(D3D_DRIVER_TYPE_WARP).unwrap();
        let cases = [
            Case {
                name: "PQ 2:1",
                source: (64, 32),
                output: (32, 16),
                pq: true,
            },
            Case {
                name: "PQ 1:1",
                source: (32, 16),
                output: (32, 16),
                pq: true,
            },
            Case {
                name: "SDR 2:1",
                source: (64, 32),
                output: (32, 16),
                pq: false,
            },
        ];
        let mut report = String::new();
        run_on(&device, &cases, 3, 1, "nostaged,bayerdither", &mut report);
        println!("{report}");
        assert!(
            report.contains("other tonemap-debug words: nostaged\n"),
            "{report}"
        );
        assert!(!report.contains("FAILS"), "{report}");
        for case in &cases {
            let line = report
                .lines()
                .find(|l| l.starts_with(&format!("  {}:", case.name)))
                .unwrap_or_else(|| panic!("no line for {}: {report}", case.name));
            for (_, _, label) in ARMS {
                assert!(line.contains(&format!(" {label} ")), "{line}");
            }
        }
        assert_eq!(
            report.matches("R2 - Bayer (medians): NV12 ").count(),
            cases.len()
        );
        assert!(!report.contains("unavailable"), "{report}");
    }
}
