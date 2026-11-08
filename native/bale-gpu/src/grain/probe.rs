//! Bounded, synthetic hardware diagnostics only; never used to select a playback route.

use std::time::{Duration, Instant};

use windows::Win32::Foundation::{E_FAIL, HMODULE, S_FALSE, S_OK};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::DXGI_ERROR_WAIT_TIMEOUT;
use windows::core::{HRESULT, Interface, Result};

use super::cpu::{self, PixelFormat};
use super::gpu::{self, BaleGrainGpu};
use super::params::ValidGrainParams;

const WARMUP_FRAMES: usize = 5;
const TIMED_FRAMES: usize = 60;
const CPU_FRAMES: usize = 10;
const QUERY_TIMEOUT: Duration = Duration::from_secs(2);
const CASES: [Case; 3] = [
    Case {
        width: 1920,
        height: 1080,
        bit_depth: 10,
    },
    Case {
        width: 3840,
        height: 2160,
        bit_depth: 10,
    },
    Case {
        width: 1920,
        height: 1080,
        bit_depth: 8,
    },
];

pub(super) fn run() -> String {
    let mut report = String::new();
    if let Err(error) = run_probe(&mut report) {
        report.push_str(&format!("  Probe: FAILS 0x{:08X}\n", error.code().0 as u32));
    }
    report
}

fn run_probe(report: &mut String) -> Result<()> {
    // All COM objects and buffers are scoped to this call, including on errors/unwinding.
    let device = ProbeDevice::hardware(report)?;
    match BaleGrainGpu::create(&device.device, &device.context) {
        Ok(mut grain) => {
            report.push_str("  Create + NV12/P010 self-tests: ok\n");
            for case in [CASES[0], CASES[2]] {
                report.push_str(&format!("  Correctness {} (CPU threads=0): ", case.label()));
                match correctness(&device, &mut grain, case) {
                    Ok(comparison) => report.push_str(&format!("{comparison}\n")),
                    Err(error) => {
                        report.push_str(&format!("FAILS 0x{:08X}\n", error.code().0 as u32));
                        // Do not benchmark a failed/possibly lost device.
                        return cpu_timings(report);
                    }
                }
            }
            report.push_str("  GPU timing: 5 warm-up + 60 timed frames; changing seed; ms/frame\n");
            for case in CASES {
                report.push_str(&format!("    {}:\n", case.label()));
                if let Err(error) = gpu_timing(&device, &mut grain, case, report) {
                    report.push_str(&format!("      Timing: {}\n", failure(error.code())));
                    // In particular, do not spend another timeout on every remaining frame/case.
                    break;
                }
            }
        }
        Err(error) => {
            report.push_str(&format!(
                "  Create + NV12/P010 self-tests: FAILS 0x{:08X}\n",
                error.code().0 as u32
            ));
            diagnostics(&device, report);
        }
    }
    cpu_timings(report)
}

struct ProbeDevice {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    multithread: ID3D10Multithread,
}

impl ProbeDevice {
    fn hardware(report: &mut String) -> Result<Self> {
        let mut device = None;
        let mut context = None;
        let mut level = D3D_FEATURE_LEVEL::default();
        // SAFETY: no adapter/software module; out-pointers are live local COM slots.
        let result = unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                Some(&mut level),
                Some(&mut context),
            )
        };
        report.push_str(&format!(
            "  Hardware D3D11 device (VIDEO_SUPPORT): {}",
            status(&result)
        ));
        if result.is_ok() {
            report.push_str(&format!(", feature level 0x{:04X}", level.0));
        }
        report.push('\n');
        result?;
        let device = device.ok_or(E_FAIL)?;
        let context = context.ok_or(E_FAIL)?;
        let multithread = context.cast::<ID3D10Multithread>();
        report.push_str(&format!("  ID3D10Multithread: {}\n", status(&multithread)));
        let multithread = multithread?;
        // SAFETY: configure this private immediate context before submitting any work.
        // The return value is the previous setting, not a success/failure indicator.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        report.push_str("  Multithread protection: on\n");
        Ok(Self {
            device,
            context,
            multithread,
        })
    }
}

/// Query operations also take the lock, outside apply's own state-isolation guard.
struct ContextLock<'a>(&'a ID3D10Multithread);

impl<'a> ContextLock<'a> {
    fn enter(multithread: &'a ID3D10Multithread) -> Self {
        // SAFETY: live interface; the returned guard pairs Enter/Leave on this thread.
        unsafe { multithread.Enter() };
        Self(multithread)
    }
}

impl Drop for ContextLock<'_> {
    fn drop(&mut self) {
        // SAFETY: paired with this guard's Enter, including during unwinding.
        unsafe { self.0.Leave() };
    }
}

fn status<T>(result: &Result<T>) -> String {
    result
        .as_ref()
        .map_or_else(|error| failure(error.code()), |_| "ok".into())
}

fn failure(hr: HRESULT) -> String {
    if hr == DXGI_ERROR_WAIT_TIMEOUT {
        format!(
            "timeout ({} ms) 0x{:08X}",
            QUERY_TIMEOUT.as_millis(),
            hr.0 as u32
        )
    } else {
        format!("FAILS 0x{:08X}", hr.0 as u32)
    }
}

fn format_name(format: DXGI_FORMAT) -> &'static str {
    match format {
        DXGI_FORMAT_NV12 => "NV12",
        DXGI_FORMAT_P010 => "P010",
        DXGI_FORMAT_R8_UINT => "R8_UINT",
        DXGI_FORMAT_R8G8_UINT => "R8G8_UINT",
        DXGI_FORMAT_R16_UINT => "R16_UINT",
        DXGI_FORMAT_R16G16_UINT => "R16G16_UINT",
        DXGI_FORMAT_R16_SINT => "R16_SINT",
        DXGI_FORMAT_R16G16_SINT => "R16G16_SINT",
        _ => "unknown",
    }
}

fn diagnostics(device: &ProbeDevice, report: &mut String) {
    report.push_str("  Create failure diagnostics:\n");
    let device1 = device.device.cast::<ID3D11Device1>();
    let context1 = device.context.cast::<ID3D11DeviceContext1>();
    report.push_str(&format!(
        "    ID3D11Device1: {}\n    ID3D11DeviceContext1: {}\n",
        status(&device1),
        status(&context1)
    ));
    for (format, required) in gpu::required_formats() {
        // SAFETY: live device and known format; this is a metadata query.
        let result = unsafe { device.device.CheckFormatSupport(format) };
        report.push_str(&format!("    CheckFormatSupport {}: ", format_name(format)));
        match result {
            Ok(bits) => report.push_str(&format!(
                "bits=0x{bits:08X}, required=0x{required:08X} {}\n",
                if bits & required == required {
                    "ok"
                } else {
                    "MISSING"
                }
            )),
            Err(error) => report.push_str(&format!("{}\n", failure(error.code()))),
        }
    }
    if let Ok(device1) = device1 {
        let mut state = None;
        // SAFETY: same flags, feature level and emulated interface as grain create; local out slot.
        let result = unsafe {
            device1.CreateDeviceContextState(
                0,
                &[device1.GetFeatureLevel()],
                D3D11_SDK_VERSION,
                &ID3D11Device::IID,
                None,
                Some(&mut state),
            )
        }
        .and_then(|()| state.ok_or_else(|| E_FAIL.into()));
        report.push_str(&format!(
            "    CreateDeviceContextState: {}\n",
            status(&result)
        ));
    } else {
        report.push_str("    CreateDeviceContextState: skipped (ID3D11Device1 unavailable)\n");
    }
    for format in [DXGI_FORMAT_NV12, DXGI_FORMAT_P010] {
        let desc = gpu::texture_desc(
            64,
            64,
            format,
            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
        );
        let texture = gpu::texture(&device.device, &desc, None);
        report.push_str(&format!(
            "    {} texture 64x64 (RENDER_TARGET|SHADER_RESOURCE): {}\n",
            format_name(format),
            status(&texture)
        ));
        if let Ok(texture) = texture {
            for view_format in gpu::plane_formats(format) {
                // Try each independently: one failure must not hide the remaining view results.
                let srv = gpu::shader_view(&device.device, &texture, view_format);
                let rtv = gpu::render_view(&device.device, &texture, view_format);
                report.push_str(&format!(
                    "      {} SRV: {}; RTV: {}\n",
                    format_name(view_format),
                    status(&srv),
                    status(&rtv)
                ));
            }
        } else {
            for view_format in gpu::plane_formats(format) {
                report.push_str(&format!(
                    "      {} SRV/RTV: skipped (texture unavailable)\n",
                    format_name(view_format)
                ));
            }
        }
    }
}

#[derive(Clone, Copy)]
struct Case {
    width: u32,
    height: u32,
    bit_depth: u8,
}

impl Case {
    fn format(self) -> DXGI_FORMAT {
        if self.bit_depth == 10 {
            DXGI_FORMAT_P010
        } else {
            DXGI_FORMAT_NV12
        }
    }

    fn pixel_format(self) -> PixelFormat {
        if self.bit_depth == 10 {
            PixelFormat::P010
        } else {
            PixelFormat::Nv12
        }
    }

    fn label(self) -> String {
        format!(
            "{} {}x{}",
            format_name(self.format()),
            self.width,
            self.height
        )
    }

    fn bytes(self) -> usize {
        if self.bit_depth == 10 { 2 } else { 1 }
    }

    fn pitch(self) -> usize {
        self.width as usize * self.bytes()
    }

    fn params(self, frame: usize) -> Result<ValidGrainParams> {
        let mut params = gpu::self_test_params(self.bit_depth, false);
        params.width = self.width;
        params.height = self.height;
        params.seed = (params.seed + frame as u32) & 0xffff;
        params.clip_to_restricted_range = 0;
        params.validate().map_err(|_| E_FAIL.into())
    }

    fn plain(self) -> Vec<u8> {
        let pitch = self.pitch();
        let mut plain = vec![0; pitch * (self.height as usize * 3 / 2)];
        for plane in 0..3 {
            let (width, height) = if plane == 0 {
                (self.width, self.height)
            } else {
                (self.width / 2, self.height / 2)
            };
            for y in 0..height as usize {
                for x in 0..width as usize {
                    let value = ((x * 37 + y * 19 + plane * 83 + (x * y) % 29)
                        & ((1 << self.bit_depth) - 1)) as u16;
                    let index = if plane == 0 {
                        y * pitch + x * self.bytes()
                    } else {
                        self.height as usize * pitch
                            + y * pitch
                            + (2 * x + plane - 1) * self.bytes()
                    };
                    if self.bit_depth == 8 {
                        plain[index] = value as u8;
                    } else {
                        plain[index..index + 2].copy_from_slice(&(value << 6).to_le_bytes());
                    }
                }
            }
        }
        plain
    }

    fn cpu_apply(self, params: &ValidGrainParams, bytes: &mut [u8], threads: u32) -> Result<()> {
        let (y, uv) = bytes.split_at_mut(self.pitch() * self.height as usize);
        cpu::apply(
            params,
            self.pixel_format(),
            y,
            self.pitch() as isize,
            uv,
            self.pitch() as isize,
            threads,
        )
        .map_err(|_| E_FAIL.into())
    }

    fn location(self, index: usize) -> (&'static str, usize, usize) {
        let row = index / self.pitch();
        let sample = index % self.pitch() / self.bytes();
        if row < self.height as usize {
            ("Y", sample, row)
        } else {
            (
                if sample.is_multiple_of(2) { "U" } else { "V" },
                sample / 2,
                row - self.height as usize,
            )
        }
    }
}

struct Frame {
    source: ID3D11Texture2D,
    destination: ID3D11Texture2D,
}

impl Frame {
    fn new(device: &ProbeDevice, case: Case, plain: &[u8]) -> Result<Self> {
        let mut desc = gpu::texture_desc(case.width, case.height, case.format(), 0);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE.0 as u32;
        let source = gpu::texture(
            &device.device,
            &desc,
            Some(&D3D11_SUBRESOURCE_DATA {
                pSysMem: plain.as_ptr().cast(),
                SysMemPitch: case.pitch() as u32,
                SysMemSlicePitch: 0,
            }),
        )?;
        let desc = gpu::texture_desc(
            case.width,
            case.height,
            case.format(),
            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
        );
        let destination = gpu::texture(&device.device, &desc, None)?;
        Ok(Self {
            source,
            destination,
        })
    }
}

fn correctness(device: &ProbeDevice, grain: &mut BaleGrainGpu, case: Case) -> Result<String> {
    let params = case.params(0)?;
    let plain = case.plain();
    let frame = Frame::new(device, case, &plain)?;
    let mut expected = plain;
    case.cpu_apply(&params, &mut expected, 0)?;
    grain.apply(&params, &frame.source, &frame.destination)?;
    let mut desc = gpu::texture_desc(case.width, case.height, case.format(), 0);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let readback = gpu::texture(&device.device, &desc, None)?;
    let actual = {
        let _lock = ContextLock::enter(&device.multithread);
        // SAFETY: matching unmapped single-subresource textures on this locked immediate context.
        unsafe { device.context.CopyResource(&readback, &frame.destination) };
        gpu::readback_bytes(
            &device.context,
            &readback,
            case.pitch(),
            case.height as usize * 3 / 2,
        )?
    };
    comparison(case, &expected, &actual)
}

fn comparison(case: Case, expected: &[u8], actual: &[u8]) -> Result<String> {
    if expected.len() != actual.len() {
        return Err(E_FAIL.into());
    }
    let mut differences = expected
        .iter()
        .zip(actual)
        .enumerate()
        .filter(|(_, (a, b))| a != b);
    if let Some((first, _)) = differences.next() {
        let count = 1 + differences.count();
        let (plane, x, y) = case.location(first);
        Ok(format!(
            "{count} differing bytes; first plane={plane} x={x} y={y}"
        ))
    } else {
        Ok("match".into())
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
            // SAFETY: known query kind, zero misc flags, live descriptor and local COM out slot.
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

    fn read(&self, device: &ProbeDevice) -> Result<Option<f64>> {
        let deadline = Instant::now() + QUERY_TIMEOUT;
        let mut disjoint = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        let mut start = 0u64;
        let mut end = 0u64;
        loop {
            let ready = {
                let _lock = ContextLock::enter(&device.multithread);
                // SAFETY: each live query was ended/flushed on this context. Each output pointer
                // and exact size match its query kind, remain live and are exclusively writable.
                unsafe {
                    query_ready(
                        &device.context,
                        &self.disjoint,
                        std::ptr::from_mut(&mut disjoint).cast(),
                        size_of_val(&disjoint) as u32,
                    )? && query_ready(
                        &device.context,
                        &self.start,
                        std::ptr::from_mut(&mut start).cast(),
                        size_of_val(&start) as u32,
                    )? && query_ready(
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
            // Never hold the context lock while waiting or spin indefinitely on a stalled GPU.
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

/// # Safety
/// `data` must be writable for `size` bytes, with the layout required by this query's kind.
/// Caller holds the immediate context's multithread lock and has ended/flushed the query.
unsafe fn query_ready(
    context: &ID3D11DeviceContext,
    query: &ID3D11Query,
    data: *mut core::ffi::c_void,
    size: u32,
) -> Result<bool> {
    // SAFETY: caller establishes query/data compatibility and exclusive output storage.
    // Use the vtable because the typed wrapper folds S_FALSE (not ready) into Ok(()).
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

fn gpu_timing(
    device: &ProbeDevice,
    grain: &mut BaleGrainGpu,
    case: Case,
    report: &mut String,
) -> Result<()> {
    let plain = case.plain();
    let frame = Frame::new(device, case, &plain)?;
    let timestamps = Timestamps::new(&device.device)?;
    for i in 0..WARMUP_FRAMES {
        grain.apply(&case.params(i)?, &frame.source, &frame.destination)?;
        let _lock = ContextLock::enter(&device.multithread);
        // SAFETY: live, locked immediate context; the probe owns submission/flush boundaries.
        unsafe { device.context.Flush() };
    }
    let mut submit = Vec::with_capacity(TIMED_FRAMES);
    let mut gpu = Vec::with_capacity(TIMED_FRAMES);
    let mut disjoint = 0;
    let mut error = None;
    for i in 0..TIMED_FRAMES {
        let params = case.params(WARMUP_FRAMES + i)?;
        {
            // ID3D10Multithread's lock is recursive; apply takes its own lock/state guard.
            let _lock = ContextLock::enter(&device.multithread);
            // SAFETY: owned queries on this context; disjoint supports Begin/End, timestamps End.
            unsafe {
                device.context.Begin(&timestamps.disjoint);
                device.context.End(&timestamps.start);
            }
            let start = Instant::now();
            let result = grain.apply(&params, &frame.source, &frame.destination);
            let elapsed = start.elapsed().as_secs_f64() * 1000.0;
            // SAFETY: close the interval even when apply fails, and flush after every frame.
            unsafe {
                device.context.End(&timestamps.end);
                device.context.End(&timestamps.disjoint);
                device.context.Flush();
            }
            result?;
            submit.push(elapsed);
        }
        match timestamps.read(device) {
            Ok(Some(ms)) => gpu.push(ms),
            Ok(None) => disjoint += 1,
            Err(failure) => {
                error = Some(failure);
                break;
            }
        }
    }
    report.push_str(&format!(
        "      CPU submit: {} ({} frames)\n",
        summary(&mut submit),
        submit.len()
    ));
    if let Some(error) = error {
        report.push_str(&format!(
            "      GPU: {} ({} completed frames)\n",
            failure(error.code()),
            gpu.len() + disjoint
        ));
        return Err(error);
    }
    if gpu.is_empty() {
        report.push_str(&format!(
            "      GPU: skipped (disjoint={disjoint}/{TIMED_FRAMES})\n"
        ));
    } else {
        report.push_str(&format!(
            "      GPU: {} ({} valid frames; disjoint={disjoint}/{TIMED_FRAMES} skipped)\n",
            summary(&mut gpu),
            gpu.len()
        ));
    }
    Ok(())
}

fn summary(samples: &mut [f64]) -> String {
    samples.sort_unstable_by(f64::total_cmp);
    let n = samples.len();
    if n == 0 {
        return "unavailable".into();
    }
    let median = (samples[(n - 1) / 2] + samples[n / 2]) / 2.0;
    let p95 = samples[(n * 95).div_ceil(100) - 1];
    format!("median={median:.3} ms, p95={p95:.3} ms")
}

fn cpu_timings(report: &mut String) -> Result<()> {
    let case = CASES[0];
    let plain = case.plain();
    let mut image = plain.clone();
    report.push_str("  CPU fallback P010 1920x1080: 10 frames each; changing seed; ms/frame\n");
    for threads in [1, 0] {
        let mut samples = Vec::with_capacity(CPU_FRAMES);
        for i in 0..CPU_FRAMES {
            let params = case.params(i)?;
            image.copy_from_slice(&plain);
            let start = Instant::now();
            case.cpu_apply(&params, &mut image, threads)?;
            samples.push(start.elapsed().as_secs_f64() * 1000.0);
        }
        samples.sort_unstable_by(f64::total_cmp);
        let median = (samples[CPU_FRAMES / 2 - 1] + samples[CPU_FRAMES / 2]) / 2.0;
        report.push_str(&format!(
            "    threads={threads}{}: median={median:.3} ms\n",
            if threads == 0 { " (auto, <=4)" } else { "" }
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn warp_probe_correctness_diagnostics_and_timestamp_polling() {
        use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};

        let mut device = None;
        let mut context = None;
        // SAFETY: no adapter/software module; output slots are live locals. Tests require WARP.
        unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_WARP,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_FLAG(0),
                Some(&[D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )
        }
        .expect("probe tests require WARP FL11_0");
        let device = device.expect("WARP device");
        let context = context.expect("WARP context");
        let multithread: ID3D10Multithread = context.cast().expect("WARP multithread interface");
        // SAFETY: configure this private context before use.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        let device = ProbeDevice {
            device,
            context,
            multithread,
        };
        let mut grain =
            BaleGrainGpu::create(&device.device, &device.context).expect("WARP grain create");
        for case in [CASES[0], CASES[2]] {
            assert_eq!(
                correctness(&device, &mut grain, case).expect("full-size readback"),
                "match"
            );
        }
        let mut report = String::new();
        diagnostics(&device, &mut report);
        assert!(
            !report.contains("FAILS") && !report.contains("MISSING"),
            "{report}"
        );
        for bit_depth in [8, 10] {
            report.clear();
            gpu_timing(
                &device,
                &mut grain,
                Case {
                    width: 64,
                    height: 64,
                    bit_depth,
                },
                &mut report,
            )
            .expect("timestamp polling");
            assert!(
                report.contains("CPU submit: median=") && report.contains("(60 frames)"),
                "{report}"
            );
            assert!(
                report.contains("GPU: median=") || report.contains("GPU: skipped (disjoint="),
                "{report}"
            );
        }
        println!(
            "GPU console-probe WARP checks: 1080p P010/NV12 match; diagnostics and timestamp polling ok"
        );
    }

    #[test]
    fn percentile_and_empty_samples() {
        assert_eq!(summary(&mut []), "unavailable");
        let mut samples: Vec<f64> = (1..=60).rev().map(f64::from).collect();
        assert_eq!(summary(&mut samples), "median=30.500 ms, p95=57.000 ms");
    }

    #[test]
    fn byte_comparison_locates_each_plane_including_both_p010_bytes() {
        for bit_depth in [8, 10] {
            let case = Case {
                width: 8,
                height: 4,
                bit_depth,
            };
            let expected = case.plain();
            assert_eq!(comparison(case, &expected, &expected).unwrap(), "match");
            for (sample, plane, x, y) in [(9, "Y", 1, 1), (42, "U", 1, 1), (43, "V", 1, 1)] {
                for byte in 0..case.bytes() {
                    let mut actual = expected.clone();
                    actual[sample * case.bytes() + byte] ^= 1;
                    assert_eq!(
                        comparison(case, &expected, &actual).unwrap(),
                        format!("1 differing bytes; first plane={plane} x={x} y={y}")
                    );
                }
            }
            let mut actual = expected.clone();
            actual[0] ^= 1;
            actual[1] ^= 1;
            assert_eq!(
                comparison(case, &expected, &actual).unwrap(),
                "2 differing bytes; first plane=Y x=0 y=0"
            );
            assert!(comparison(case, &expected, &actual[..actual.len() - 1]).is_err());
            let params = case.params(0).unwrap();
            assert_eq!(params.as_raw().overlap_flag, 1);
            assert_eq!(params.as_raw().ar_coeff_lag, 3);
            assert_eq!(params.as_raw().clip_to_restricted_range, 0);
            assert!(params.chroma_active(0) && params.chroma_active(1));
            assert!(case.params(1).unwrap().as_raw().seed != params.as_raw().seed);
        }
    }
}
