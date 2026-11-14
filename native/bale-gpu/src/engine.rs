//! Debug IMFMediaEngine frame-server probe, a typed port of the C# `DebugMediaEngine` spike.
//!
//! Plays a public URL muted, transfers each new frame into our own BGRA texture on our own D3D11
//! device (Xbox-ATG-Samples UWPSamples/Graphics/VideoTextureUWP, MediaEnginePlayer.cpp), then reports
//! the engine's own render/drop statistics. The report never contains the URL or system error text:
//! only step names and HRESULTs.
//!
//! `Input::Source` plays a caller-supplied COM object instead (FFmpegInteropX's MediaStreamSource):
//! it must expose `IMFMediaSource`, which an `IMFMediaEngineExtension` hands to the engine for a
//! private URL, as Chromium's MediaFoundationRenderer does (media/renderers/win/media_engine_extension.cc).
//!
//! `Target` picks the output format (`MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT` and the destination
//! texture alike, the only documented lever). For fp16 and rgb10 a centred crop is read back about
//! once a second to see whether HDR10 range survives `TransferVideoFrame`: phase 0 of GPU tone mapping.

use std::fmt::Write as _;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{FILETIME, HANDLE, HMODULE, RECT};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX, D3D11_CPU_ACCESS_READ,
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
    D3D11_FORMAT_SUPPORT_RENDER_TARGET, D3D11_FORMAT_SUPPORT_TEXTURE2D,
    D3D11_FORMAT_SUPPORT_VIDEO_PROCESSOR_OUTPUT, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE,
    D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};
use windows::Win32::Media::MediaFoundation::{
    CLSID_MFMediaEngineClassFactory, IMFAsyncCallback, IMFAsyncResult, IMFAttributes,
    IMFByteStream, IMFDXGIDeviceManager, IMFGetService, IMFMediaEngine, IMFMediaEngineClassFactory,
    IMFMediaEngineEx, IMFMediaEngineExtension, IMFMediaEngineExtension_Impl, IMFMediaEngineNotify,
    IMFMediaEngineNotify_Impl, IMFMediaSource, IMFMediaSourceEx, MF_E_UNEXPECTED,
    MF_MEDIA_ENGINE_CALLBACK, MF_MEDIA_ENGINE_CANPLAY, MF_MEDIA_ENGINE_CANPLAY_MAYBE,
    MF_MEDIA_ENGINE_DXGI_MANAGER, MF_MEDIA_ENGINE_EXTENSION, MF_MEDIA_ENGINE_STATISTIC,
    MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, MF_MEDIASOURCE_SERVICE, MF_OBJECT_MEDIASOURCE,
    MF_OBJECT_TYPE, MFARGB, MFCreateAsyncResult, MFCreateAttributes, MFCreateDXGIDeviceManager,
    MFInvokeCallback, MFSTARTUP_FULL, MFShutdown, MFStartup,
};
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes, SetEvent};
use windows::core::{BOOL, BSTR, GUID, HRESULT, IUnknown, Interface, OutRef, Ref, implement};

use crate::engine_target::{Sampler, Target, centred_crop, format_name};

const MF_VERSION: u32 = 0x0002_0070; // MF_SDK_VERSION 2 << 16 | MF_API_VERSION 0x70
const OPEN_TIMEOUT: Duration = Duration::from_secs(20);
/// Gap between HDR readbacks: often enough to catch a highlight, rare enough not to disturb playback.
const READBACK_INTERVAL: Duration = Duration::from_secs(1);

const STATISTICS: [(i32, &str); 7] = [
    (0, "frames rendered"),
    (1, "frames dropped"),
    (6, "frames corrupted"),
    (4, "frames per second"),
    (5, "playback jitter"),
    (7, "total frame delay"),
    (2, "bytes downloaded"),
];

// combase.dll, as the C# probe used: windows-rs links CoCreateInstance from ole32.dll.
#[link(name = "combase", kind = "raw-dylib")]
unsafe extern "system" {
    fn CoCreateInstance(
        clsid: *const GUID,
        outer: *mut core::ffi::c_void,
        context: u32,
        iid: *const GUID,
        instance: *mut *mut core::ffi::c_void,
    ) -> HRESULT;
}

/// State the engine's callback writes from its own threads.
struct Shared {
    clock: Instant,
    can_play: AtomicBool,
    error: AtomicBool,
    ended: AtomicBool,
    first_frame: AtomicBool,
    events: Mutex<Vec<(u32, u128)>>,
    /// Object types the extension was asked for (source input only).
    extension_requests: Mutex<Vec<i32>>,
}

/// What the probe plays.
pub enum Input<'a> {
    /// A public http(s) URL, UTF-16.
    Url(&'a [u16]),
    /// A COM object that should expose `IMFMediaSource`.
    Source(IUnknown),
}

/// Hands the caller's media source to the engine for any URL (only the private one is ever set).
#[implement(IMFMediaEngineExtension)]
struct SourceExtension {
    source: IMFMediaSource,
    shared: Arc<Shared>,
}

impl IMFMediaEngineExtension_Impl for SourceExtension_Impl {
    fn CanPlayType(
        &self,
        _audio_only: BOOL,
        _mime_type: &BSTR,
    ) -> windows::core::Result<MF_MEDIA_ENGINE_CANPLAY> {
        Ok(MF_MEDIA_ENGINE_CANPLAY_MAYBE)
    }

    fn BeginCreateObject(
        &self,
        _url: &BSTR,
        _byte_stream: Ref<IMFByteStream>,
        kind: MF_OBJECT_TYPE,
        _cancel_cookie: OutRef<IUnknown>,
        callback: Ref<IMFAsyncCallback>,
        state: Ref<IUnknown>,
    ) -> windows::core::Result<()> {
        if let Ok(mut requests) = self.shared.extension_requests.lock() {
            requests.push(kind.0);
        }
        if kind != MF_OBJECT_MEDIASOURCE {
            return Err(MF_E_UNEXPECTED.into());
        }
        // SAFETY: the async result holds its own references to the source, callback and state.
        unsafe {
            let result = MFCreateAsyncResult(&self.source, callback.as_ref(), state.as_ref())?;
            result.SetStatus(HRESULT(0))?;
            MFInvokeCallback(&result)
        }
    }

    fn CancelObjectCreation(&self, _cancel_cookie: Ref<IUnknown>) -> windows::core::Result<()> {
        Ok(())
    }

    fn EndCreateObject(&self, result: Ref<IMFAsyncResult>) -> windows::core::Result<IUnknown> {
        let result = result.ok()?;
        // SAFETY: `result` is the async result created in BeginCreateObject.
        unsafe {
            result.GetStatus()?;
            result.GetObject()
        }
    }
}

#[implement(IMFMediaEngineNotify)]
struct Notify(Arc<Shared>);

impl IMFMediaEngineNotify_Impl for Notify_Impl {
    fn EventNotify(&self, event: u32, param1: usize, _param2: u32) -> windows::core::Result<()> {
        let shared = &self.0;
        match event {
            // The engine waits on this handle when created with WAITFORSTABLE_STATE; signal it as the sample does.
            1008 => {
                // SAFETY: for NOTIFYSTABLESTATE, param1 is an event handle owned by the engine.
                let _ = unsafe { SetEvent(HANDLE(param1 as *mut core::ffi::c_void)) };
                return Ok(());
            }
            14 => shared.can_play.store(true, Ordering::Release),
            5 => shared.error.store(true, Ordering::Release),
            19 => shared.ended.store(true, Ordering::Release),
            1009 => shared.first_frame.store(true, Ordering::Release),
            _ => {}
        }
        if let Ok(mut events) = shared.events.lock()
            && events.len() < 400
        {
            events.push((event, shared.clock.elapsed().as_millis()));
        }
        Ok(())
    }
}

/// A failed step: its name and HRESULT, nothing else.
struct Step(&'static str, HRESULT);

trait StepResult<T> {
    fn step(self, name: &'static str) -> Result<T, Step>;
}

impl<T> StepResult<T> for windows::core::Result<T> {
    fn step(self, name: &'static str) -> Result<T, Step> {
        self.map_err(|error| Step(name, error.code()))
    }
}

/// Reads a centred crop of the frame texture back through a staging texture and gathers its
/// statistics (fp16 and rgb10 only). Timed apart from `TransferVideoFrame`.
struct Readback {
    context: ID3D11DeviceContext,
    staging: ID3D11Texture2D,
    region: D3D11_BOX,
    frame: (u32, u32),
    row_bytes: usize,
    sampler: Sampler,
    copy_total: Duration,
    copy_max: Duration,
    scan_total: Duration,
    scan_max: Duration,
    failures: u32,
    first_failure: Step,
}

impl Readback {
    unsafe fn new(
        device: &ID3D11Device,
        sampler: Sampler,
        target: Target,
        width: u32,
        height: u32,
    ) -> Result<Self, Step> {
        let (left, top, crop_width, crop_height) = centred_crop(width, height);
        let desc = D3D11_TEXTURE2D_DESC {
            Width: crop_width,
            Height: crop_height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT(target.dxgi_format() as i32),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_STAGING,
            BindFlags: 0,
            CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
            MiscFlags: 0,
        };
        let mut staging: Option<ID3D11Texture2D> = None;
        // SAFETY: `desc` and the out-pointer are valid locals.
        unsafe { device.CreateTexture2D(&desc, None, Some(&mut staging)) }
            .step("CreateTexture2D staging")?;
        let staging = staging.ok_or(Step(
            "CreateTexture2D staging",
            HRESULT(0x8000_4003u32 as i32),
        ))?;
        // SAFETY: the device is alive.
        let context = unsafe { device.GetImmediateContext() }.step("GetImmediateContext")?;
        Ok(Self {
            context,
            staging,
            region: D3D11_BOX {
                left,
                top,
                front: 0,
                right: left + crop_width,
                bottom: top + crop_height,
                back: 1,
            },
            frame: (width, height),
            row_bytes: crop_width as usize * target.bytes_per_pixel(),
            sampler,
            copy_total: Duration::ZERO,
            copy_max: Duration::ZERO,
            scan_total: Duration::ZERO,
            scan_max: Duration::ZERO,
            failures: 0,
            first_failure: Step("", HRESULT(0)),
        })
    }

    fn fail(&mut self, step: Step) {
        if self.failures == 0 {
            self.first_failure = step;
        }
        self.failures += 1;
    }

    /// Copies the crop of `texture` (the frame just transferred) and scans it.
    unsafe fn sample(&mut self, texture: &ID3D11Texture2D) {
        let started = Instant::now();
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        // SAFETY: both textures belong to this context's device; the box lies inside the frame.
        // Map blocks until the GPU has finished the copy, and the frame transfer before it.
        let map = unsafe {
            self.context.CopySubresourceRegion(
                &self.staging,
                0,
                0,
                0,
                0,
                texture,
                0,
                Some(&self.region),
            );
            self.context
                .Map(&self.staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
        };
        if let Err(error) = map {
            self.fail(Step("Map staging", error.code()));
            return;
        }
        let copied = started.elapsed();
        let rows = (self.region.bottom - self.region.top) as usize;
        let pitch = mapped.RowPitch as usize;
        if mapped.pData.is_null() || pitch < self.row_bytes {
            self.fail(Step("Map staging layout", HRESULT(0x8000_FFFFu32 as i32)));
        } else {
            self.sampler.begin_sample();
            for row in 0..rows {
                // SAFETY: a mapped staging texture has `rows` rows of `pitch` bytes, each holding
                // at least `row_bytes` of pixels, readable until Unmap.
                let bytes = unsafe {
                    std::slice::from_raw_parts(
                        mapped.pData.cast::<u8>().add(row * pitch),
                        self.row_bytes,
                    )
                };
                self.sampler.add_row(bytes);
            }
            self.sampler.end_sample();
        }
        // SAFETY: the staging texture is mapped by the call above.
        unsafe { self.context.Unmap(&self.staging, 0) };
        let scanned = started.elapsed() - copied;
        self.copy_total += copied;
        self.copy_max = self.copy_max.max(copied);
        self.scan_total += scanned;
        self.scan_max = self.scan_max.max(scanned);
    }

    fn append(&self, report: &mut String) {
        let samples = self.sampler.samples();
        let _ = write!(
            report,
            "Readback: {samples} samples, crop {}x{} at ({},{}) of {}x{}",
            self.region.right - self.region.left,
            self.region.bottom - self.region.top,
            self.region.left,
            self.region.top,
            self.frame.0,
            self.frame.1
        );
        if samples > 0 {
            let mean = |total: Duration| total.as_secs_f64() * 1000.0 / f64::from(samples);
            let _ = write!(
                report,
                "; copy+map mean {:.3} ms, max {:.3} ms; scan mean {:.3} ms, max {:.3} ms",
                mean(self.copy_total),
                self.copy_max.as_secs_f64() * 1000.0,
                mean(self.scan_total),
                self.scan_max.as_secs_f64() * 1000.0
            );
        }
        if self.failures > 0 {
            let _ = write!(
                report,
                "; failures: {} (first 0x{:08X} at {})",
                self.failures, self.first_failure.1.0 as u32, self.first_failure.0
            );
        }
        report.push_str(
            " (not part of the TransferVideoFrame timing; copy+map waits for the GPU to finish the frame transfer)\n",
        );
        report.push_str(&self.sampler.report());
    }
}

/// Runs the probe and returns the plain-text report.
pub fn run(input: Input, seconds: u32, target: Target) -> String {
    let mut report = String::from(match input {
        Input::Url(_) => {
            "Media engine probe (Rust bale_gpu.dll; frame-server mode, muted, own D3D11 device)\n"
        }
        Input::Source(_) => {
            "Media engine probe (Rust bale_gpu.dll; caller media source via engine extension; frame-server mode, muted)\n"
        }
    });
    let _ = writeln!(
        report,
        "Target: {} (DXGI_FORMAT_{} = {}) requested as MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT and as the destination texture format",
        target.name(),
        target.format_name(),
        target.dxgi_format()
    );
    let shared = Arc::new(Shared {
        clock: Instant::now(),
        can_play: AtomicBool::new(false),
        error: AtomicBool::new(false),
        ended: AtomicBool::new(false),
        first_frame: AtomicBool::new(false),
        events: Mutex::new(Vec::new()),
        extension_requests: Mutex::new(Vec::new()),
    });
    // SAFETY: plain MF startup; paired with MFShutdown below only on success.
    if let Err(error) = unsafe { MFStartup(MF_VERSION, MFSTARTUP_FULL) } {
        let _ = writeln!(
            report,
            "Error: 0x{:08X} at MFStartup",
            error.code().0 as u32
        );
        return report;
    }
    let mut engine: Option<IMFMediaEngine> = None;
    let mut device_lifetime = None;
    // SAFETY: every COM object is created and used on this thread inside `play`.
    let outcome = unsafe {
        play(
            &input,
            seconds,
            target,
            &shared,
            &mut engine,
            &mut device_lifetime,
            &mut report,
        )
    };
    if let Err(Step(name, hr)) = outcome {
        let _ = writeln!(report, "Error: 0x{:08X} at {name}", hr.0 as u32);
        if target != Target::Bgra {
            let _ = writeln!(
                report,
                "Target {}: stopped at {name} (0x{:08X}); no frame reached the {} texture",
                target.name(),
                hr.0 as u32,
                target.format_name()
            );
        }
    }
    if target != Target::Bgra && !report.contains("hdr-range=") {
        // Open failure, no video, or a refused step: nothing was read back.
        let _ = writeln!(
            report,
            "hdr-range=unknown ({} target, the probe stopped before any readback)",
            target.name()
        );
    }
    if let Some(engine) = engine.take() {
        // SAFETY: the engine is still alive; Shutdown precedes its final release.
        unsafe {
            append_error(&mut report, &engine);
            let _ = engine.Shutdown();
        }
    }
    drop(device_lifetime);
    append_events(&mut report, &shared);
    drop(input);
    // SAFETY: all MF objects created after MFStartup have been released above.
    unsafe {
        let _ = MFShutdown();
    }
    report
}

unsafe fn play(
    input: &Input,
    seconds: u32,
    target: Target,
    shared: &Arc<Shared>,
    engine_slot: &mut Option<IMFMediaEngine>,
    device_lifetime: &mut Option<Arc<crate::lifecycle::Device>>,
    report: &mut String,
) -> Result<(), Step> {
    unsafe {
        let mut device: Option<ID3D11Device> = None;
        let mut level = D3D_FEATURE_LEVEL::default();
        // Hardware only, never WARP.
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            Some(&mut level),
            None,
        )
        .step("D3D11CreateDevice")?;
        let device = device.ok_or(Step("D3D11CreateDevice", HRESULT(0x8000_4003u32 as i32)))?;
        *device_lifetime = Some(crate::lifecycle::Device::track(&device));
        let _ = writeln!(report, "D3D11 feature level: 0x{:04X}", level.0);
        let format = DXGI_FORMAT(target.dxgi_format() as i32);
        match device.CheckFormatSupport(format) {
            Ok(support) => {
                let has = |flag: i32| support & flag as u32 != 0;
                let _ = writeln!(
                    report,
                    "Format support ({}): 0x{support:08X} texture2d={} render target={} video processor output={}",
                    target.format_name(),
                    has(D3D11_FORMAT_SUPPORT_TEXTURE2D.0),
                    has(D3D11_FORMAT_SUPPORT_RENDER_TARGET.0),
                    has(D3D11_FORMAT_SUPPORT_VIDEO_PROCESSOR_OUTPUT.0)
                );
            }
            Err(error) => {
                let _ = writeln!(
                    report,
                    "Format support ({}): CheckFormatSupport 0x{:08X}",
                    target.format_name(),
                    error.code().0 as u32
                );
            }
        }
        let multithread: ID3D10Multithread = device.cast().step("QI ID3D10Multithread")?;
        let _ = multithread.SetMultithreadProtected(true);

        let mut token = 0u32;
        let mut manager: Option<IMFDXGIDeviceManager> = None;
        MFCreateDXGIDeviceManager(&mut token, &mut manager).step("MFCreateDXGIDeviceManager")?;
        let manager = manager.ok_or(Step(
            "MFCreateDXGIDeviceManager",
            HRESULT(0x8000_4003u32 as i32),
        ))?;
        manager.ResetDevice(&device, token).step("ResetDevice")?;

        let mut attributes: Option<IMFAttributes> = None;
        MFCreateAttributes(&mut attributes, 3).step("MFCreateAttributes")?;
        let attributes =
            attributes.ok_or(Step("MFCreateAttributes", HRESULT(0x8000_4003u32 as i32)))?;
        let notify: IMFMediaEngineNotify = Notify(Arc::clone(shared)).into();
        let url = match input {
            Input::Url(url) => BSTR::from_wide(url),
            Input::Source(object) => {
                let source = object.cast::<IMFMediaSource>();
                let _ = writeln!(
                    report,
                    "Source interfaces: IMFMediaSource={} IMFMediaSourceEx={} IMFGetService={}",
                    source.is_ok(),
                    object.cast::<IMFMediaSourceEx>().is_ok(),
                    object.cast::<IMFGetService>().is_ok()
                );
                // A WinRT media source exposes its IMFMediaSource as a service rather than through QI.
                let source = match source {
                    Ok(source) => source,
                    Err(_) => {
                        let service = object
                            .cast::<IMFGetService>()
                            .step("QI IMFGetService")?
                            .GetService::<IMFMediaSource>(&MF_MEDIASOURCE_SERVICE);
                        let _ = writeln!(
                            report,
                            "GetService(MF_MEDIASOURCE_SERVICE): {}",
                            if service.is_ok() { "ok" } else { "fails" }
                        );
                        service.step("GetService MF_MEDIASOURCE_SERVICE")?
                    }
                };
                let extension: IMFMediaEngineExtension = SourceExtension {
                    source,
                    shared: Arc::clone(shared),
                }
                .into();
                attributes
                    .SetUnknown(&MF_MEDIA_ENGINE_EXTENSION, &extension)
                    .step("SetUnknown extension")?;
                BSTR::from("bale-mss:probe")
            }
        };
        attributes
            .SetUnknown(&MF_MEDIA_ENGINE_DXGI_MANAGER, &manager)
            .step("SetUnknown DXGI manager")?;
        attributes
            .SetUnknown(&MF_MEDIA_ENGINE_CALLBACK, &notify)
            .step("SetUnknown callback")?;
        attributes
            .SetUINT32(&MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, target.dxgi_format())
            .step("SetUINT32 output format")?;

        let mut raw = core::ptr::null_mut();
        CoCreateInstance(
            &CLSID_MFMediaEngineClassFactory,
            core::ptr::null_mut(),
            1, // CLSCTX_INPROC_SERVER
            &IMFMediaEngineClassFactory::IID,
            &mut raw,
        )
        .ok()
        .step("CoCreateInstance MediaEngineClassFactory")?;
        let factory = IMFMediaEngineClassFactory::from_raw(raw);
        let engine = factory
            .CreateInstance(0, &attributes)
            .step("CreateInstance")?;
        *engine_slot = Some(engine.clone());
        let engine_ex: IMFMediaEngineEx = engine.cast().step("QI IMFMediaEngineEx")?;
        report.push_str("Engine: created\n");

        engine.SetMuted(true).step("SetMuted")?;
        engine.SetSource(&url).step("SetSource")?;

        let open = Instant::now();
        while !shared.can_play.load(Ordering::Acquire)
            && !shared.error.load(Ordering::Acquire)
            && open.elapsed() < OPEN_TIMEOUT
        {
            std::thread::sleep(Duration::from_millis(10));
        }
        let can_play = shared.can_play.load(Ordering::Acquire);
        let state = if can_play {
            "canplay"
        } else if shared.error.load(Ordering::Acquire) {
            "error"
        } else {
            "timeout"
        };
        let _ = writeln!(
            report,
            "Open: {state} after {} ms",
            open.elapsed().as_millis()
        );
        if !can_play {
            return Ok(());
        }

        let (mut width, mut height) = (0u32, 0u32);
        let _ = engine.GetNativeVideoSize(Some(&mut width), Some(&mut height));
        let has_video = engine.HasVideo().as_bool();
        let has_audio = engine.HasAudio().as_bool();
        let duration = engine.GetDuration();
        let _ = writeln!(
            report,
            "Video: {width}x{height} hasVideo={} hasAudio={} duration={}s",
            has_video,
            has_audio,
            trim(duration, 3)
        );
        if !has_video || width == 0 || height == 0 {
            return Ok(());
        }

        let desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut texture: Option<ID3D11Texture2D> = None;
        device
            .CreateTexture2D(&desc, None, Some(&mut texture))
            .step("CreateTexture2D")?;
        let texture = texture.ok_or(Step("CreateTexture2D", HRESULT(0x8000_4003u32 as i32)))?;
        // What is really in use, read back rather than assumed.
        let mut created = D3D11_TEXTURE2D_DESC::default();
        texture.GetDesc(&mut created);
        let _ = write!(
            report,
            "Format used: texture {}x{} {} ({})",
            created.Width,
            created.Height,
            format_name(created.Format.0 as u32),
            created.Format.0
        );
        match attributes.GetUINT32(&MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT) {
            Ok(value) => {
                let _ = writeln!(
                    report,
                    "; engine output format attribute {} ({value})",
                    format_name(value)
                );
            }
            Err(error) => {
                let _ = writeln!(
                    report,
                    "; engine output format attribute unreadable 0x{:08X}",
                    error.code().0 as u32
                );
            }
        }
        let mut readback = match Sampler::new(target) {
            None => None,
            Some(sampler) => match Readback::new(&device, sampler, target, width, height) {
                Ok(readback) => Some(readback),
                Err(Step(name, hr)) => {
                    let _ = writeln!(
                        report,
                        "Readback: unavailable, 0x{:08X} at {name}",
                        hr.0 as u32
                    );
                    None
                }
            },
        };

        let cpu_before = process_cpu();
        let start_position = engine.GetCurrentTime();
        engine.Play().step("Play")?;
        let wall = Instant::now();
        let limit = Duration::from_secs(u64::from(seconds));
        let (mut polls, mut ticks, mut frames, mut failures) = (0u64, 0u64, 0u64, 0u64);
        let mut first_failure = HRESULT(0);
        let (mut transfer_total, mut transfer_max) = (Duration::ZERO, Duration::ZERO);
        // The first frame is sampled at once, then one about every second, then the last frame.
        let mut next_readback = wall;
        let mut sampled_frame = 0u64;
        let destination = RECT {
            left: 0,
            top: 0,
            right: width as i32,
            bottom: height as i32,
        };
        let border = MFARGB::default(); // transparent black
        let tick = Interface::vtable(&engine).OnVideoStreamTick;
        while wall.elapsed() < limit
            && !shared.error.load(Ordering::Acquire)
            && !shared.ended.load(Ordering::Acquire)
        {
            polls += 1;
            let mut pts = 0i64;
            // Raw call: the typed wrapper folds S_FALSE ("no new frame") into Ok.
            if tick(engine.as_raw(), &mut pts) == HRESULT(0) {
                ticks += 1;
                let started = Instant::now();
                let result = engine.TransferVideoFrame(&texture, None, &destination, Some(&border));
                let elapsed = started.elapsed();
                transfer_total += elapsed;
                transfer_max = transfer_max.max(elapsed);
                match result {
                    Ok(()) => {
                        frames += 1;
                        // After the transfer timing above, so the readback never counts towards it.
                        if let Some(readback) = &mut readback
                            && Instant::now() >= next_readback
                        {
                            readback.sample(&texture);
                            sampled_frame = frames;
                            next_readback = Instant::now() + READBACK_INTERVAL;
                        }
                    }
                    Err(error) => {
                        if failures == 0 {
                            first_failure = error.code();
                        }
                        failures += 1;
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        let wall_seconds = wall.elapsed().as_secs_f64();
        let end_position = engine.GetCurrentTime();
        let cpu_after = process_cpu();
        // The last frame, after the CPU and position readings so it does not skew them.
        if let Some(readback) = &mut readback
            && frames > sampled_frame
        {
            readback.sample(&texture);
        }

        let _ = writeln!(
            report,
            "Poll loop: {polls} polls ({:.0}/s; thread::sleep(1 ms))",
            polls as f64 / wall_seconds
        );
        let _ = write!(
            report,
            "Stream ticks: {ticks}; transferred frames: {frames}; transfer failures: {failures}"
        );
        if failures > 0 {
            let _ = write!(report, " (first 0x{:08X})", first_failure.0 as u32);
        }
        report.push('\n');
        if failures > 0 && target != Target::Bgra {
            let _ = writeln!(
                report,
                "Target {}: TransferVideoFrame failed {failures} of {ticks} times (first 0x{:08X})",
                target.name(),
                first_failure.0 as u32
            );
        }
        if ticks > 0 {
            let _ = writeln!(
                report,
                "TransferVideoFrame: mean {:.3} ms, max {:.3} ms",
                transfer_total.as_secs_f64() * 1000.0 / ticks as f64,
                transfer_max.as_secs_f64() * 1000.0
            );
        }
        let advanced = end_position - start_position;
        let _ = writeln!(
            report,
            "Position: start={}s end={}s wall={wall_seconds:.3}s ratio={:.3}",
            trim(start_position, 3),
            trim(end_position, 3),
            advanced / wall_seconds
        );
        if let (Some(before), Some(after)) = (cpu_before, cpu_after) {
            let _ = writeln!(
                report,
                "CPU: {:.3} cores (process)",
                (after - before) / wall_seconds
            );
        }
        append_statistics(report, &engine_ex);
        if let Some(readback) = &readback {
            readback.append(report);
        }
        report.push_str(if shared.error.load(Ordering::Acquire) {
            "Verdict: FAILS - engine error\n"
        } else if advanced <= 0.0 {
            "Verdict: FAILS - position did not advance\n"
        } else if frames == 0 {
            "Verdict: FAILS - no frames transferred\n"
        } else if advanced / wall_seconds < 0.9 {
            "Verdict: SLOW - below real time (ratio < 0.9)\n"
        } else {
            "Verdict: PLAYS\n"
        });
        Ok(())
    }
}

unsafe fn append_statistics(report: &mut String, engine_ex: &IMFMediaEngineEx) {
    report.push_str("Engine statistics:");
    for (id, name) in STATISTICS {
        // SAFETY: GetStatistics fills a PROPVARIANT; numeric variants own nothing, so no PropVariantClear.
        let text = match unsafe { engine_ex.GetStatistics(MF_MEDIA_ENGINE_STATISTIC(id)) } {
            Err(error) => format!("0x{:08X}", error.code().0 as u32),
            Ok(value) => unsafe {
                let inner = &value.Anonymous.Anonymous;
                match inner.vt.0 {
                    3 | 19 => inner.Anonymous.ulVal.to_string(),
                    20 | 21 => inner.Anonymous.uhVal.to_string(),
                    4 => trim(f64::from(inner.Anonymous.fltVal), 3),
                    5 => trim(inner.Anonymous.dblVal, 3),
                    0 => "empty".to_string(),
                    other => format!("vt={other}"),
                }
            },
        };
        let _ = write!(report, " {name}={text};");
    }
    report.push('\n');
}

unsafe fn append_error(report: &mut String, engine: &IMFMediaEngine) {
    // SAFETY: the engine is alive; GetError returns an owned IMFMediaError or fails.
    if let Ok(error) = unsafe { engine.GetError() } {
        let code = unsafe { error.GetErrorCode() };
        let extended = match unsafe { error.GetExtendedErrorCode() } {
            Ok(()) => 0u32,
            Err(error) => error.code().0 as u32,
        };
        if code != 0 || extended != 0 {
            let _ = writeln!(
                report,
                "Engine error: code={code} extended=0x{extended:08X}"
            );
        }
    }
}

fn append_events(report: &mut String, shared: &Shared) {
    let events = shared
        .events
        .lock()
        .map(|events| events.clone())
        .unwrap_or_default();
    let listed: Vec<String> = events
        .iter()
        .filter(|(event, _)| !matches!(event, 2 | 18))
        .take(40)
        .map(|(event, ms)| format!("{}@{ms}", event_name(*event)))
        .collect();
    let _ = writeln!(
        report,
        "Events (progress/timeupdate omitted): {}",
        listed.join(" ")
    );
    let requests = shared
        .extension_requests
        .lock()
        .map(|requests| requests.clone())
        .unwrap_or_default();
    if !requests.is_empty() {
        let kinds: Vec<String> = requests.iter().map(|kind| kind.to_string()).collect();
        let _ = writeln!(
            report,
            "Extension requests (0=media source, 1=byte stream): {}",
            kinds.join(" ")
        );
    }
    let _ = writeln!(
        report,
        "First frame ready: {}",
        if shared.first_frame.load(Ordering::Acquire) {
            "True"
        } else {
            "False"
        }
    );
}

/// Process kernel+user CPU time in seconds.
fn process_cpu() -> Option<f64> {
    let (mut creation, mut exit, mut kernel, mut user) = (
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
    );
    // SAFETY: GetCurrentProcess is a pseudo-handle; all out-pointers are valid locals.
    unsafe {
        GetProcessTimes(
            GetCurrentProcess(),
            &mut creation,
            &mut exit,
            &mut kernel,
            &mut user,
        )
    }
    .ok()?;
    let ticks =
        |time: FILETIME| (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
    Some((ticks(kernel) + ticks(user)) as f64 / 10_000_000.0)
}

/// Formats like .NET "0.###": up to `places` decimals, trailing zeros trimmed.
fn trim(value: f64, places: usize) -> String {
    let text = format!("{value:.places$}");
    if text.contains('.') {
        text.trim_end_matches('0').trim_end_matches('.').to_string()
    } else {
        text
    }
}

fn event_name(id: u32) -> String {
    let name = match id {
        1 => "loadstart",
        3 => "suspend",
        4 => "abort",
        5 => "ERROR",
        6 => "emptied",
        7 => "stalled",
        8 => "play",
        9 => "pause",
        10 => "loadedmetadata",
        11 => "loadeddata",
        12 => "waiting",
        13 => "playing",
        14 => "canplay",
        15 => "canplaythrough",
        16 => "seeking",
        17 => "seeked",
        19 => "ended",
        20 => "ratechange",
        21 => "durationchange",
        22 => "volumechange",
        1000 => "formatchange",
        1005 => "bufferingstarted",
        1006 => "bufferingended",
        1008 => "stablestate",
        1009 => "firstframeready",
        1010 => "trackschange",
        1012 => "resourcelost",
        1014 => "streamrenderingerror",
        1015 => "ratesupported",
        _ => return id.to_string(),
    };
    name.to_string()
}
