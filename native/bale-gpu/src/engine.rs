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

use std::fmt::Write as _;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{FILETIME, HANDLE, HMODULE, RECT};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
    D3D11CreateDevice, ID3D11Device, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Media::MediaFoundation::{
    CLSID_MFMediaEngineClassFactory, IMFAsyncCallback, IMFAsyncResult, IMFAttributes, IMFByteStream,
    IMFDXGIDeviceManager, IMFGetService, IMFMediaEngine, IMFMediaEngineClassFactory, IMFMediaEngineEx,
    IMFMediaEngineExtension, IMFMediaEngineExtension_Impl, IMFMediaEngineNotify, IMFMediaEngineNotify_Impl,
    IMFMediaSource, IMFMediaSourceEx, MF_E_UNEXPECTED, MF_MEDIASOURCE_SERVICE, MF_MEDIA_ENGINE_CALLBACK, MF_MEDIA_ENGINE_CANPLAY,
    MF_MEDIA_ENGINE_CANPLAY_MAYBE, MF_MEDIA_ENGINE_DXGI_MANAGER, MF_MEDIA_ENGINE_EXTENSION, MF_MEDIA_ENGINE_STATISTIC,
    MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, MF_OBJECT_MEDIASOURCE, MF_OBJECT_TYPE, MFARGB, MFCreateAsyncResult,
    MFCreateAttributes, MFCreateDXGIDeviceManager, MFInvokeCallback, MFSTARTUP_FULL, MFShutdown, MFStartup,
};
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes, SetEvent};
use windows::core::{BOOL, BSTR, GUID, HRESULT, IUnknown, Interface, OutRef, Ref, implement};

const MF_VERSION: u32 = 0x0002_0070; // MF_SDK_VERSION 2 << 16 | MF_API_VERSION 0x70
const OPEN_TIMEOUT: Duration = Duration::from_secs(20);

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
    fn CanPlayType(&self, _audio_only: BOOL, _mime_type: &BSTR) -> windows::core::Result<MF_MEDIA_ENGINE_CANPLAY> {
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
        if let Ok(mut events) = shared.events.lock() {
            if events.len() < 400 {
                events.push((event, shared.clock.elapsed().as_millis()));
            }
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

/// Runs the probe and returns the plain-text report.
pub fn run(input: Input, seconds: u32) -> String {
    let mut report = String::from(match input {
        Input::Url(_) => "Media engine probe (Rust bale_gpu.dll; frame-server mode, muted, own D3D11 device)\n",
        Input::Source(_) => "Media engine probe (Rust bale_gpu.dll; caller media source via engine extension; frame-server mode, muted)\n",
    });
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
        let _ = writeln!(report, "Error: 0x{:08X} at MFStartup", error.code().0 as u32);
        return report;
    }
    let mut engine: Option<IMFMediaEngine> = None;
    // SAFETY: every COM object is created and used on this thread inside `play`.
    let outcome = unsafe { play(&input, seconds, &shared, &mut engine, &mut report) };
    if let Err(Step(name, hr)) = outcome {
        let _ = writeln!(report, "Error: 0x{:08X} at {name}", hr.0 as u32);
    }
    if let Some(engine) = engine.take() {
        // SAFETY: the engine is still alive; Shutdown precedes its final release.
        unsafe {
            append_error(&mut report, &engine);
            let _ = engine.Shutdown();
        }
    }
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
    shared: &Arc<Shared>,
    engine_slot: &mut Option<IMFMediaEngine>,
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
        let _ = writeln!(report, "D3D11 feature level: 0x{:04X}", level.0);
        let multithread: ID3D10Multithread = device.cast().step("QI ID3D10Multithread")?;
        let _ = multithread.SetMultithreadProtected(true);

        let mut token = 0u32;
        let mut manager: Option<IMFDXGIDeviceManager> = None;
        MFCreateDXGIDeviceManager(&mut token, &mut manager).step("MFCreateDXGIDeviceManager")?;
        let manager = manager.ok_or(Step("MFCreateDXGIDeviceManager", HRESULT(0x8000_4003u32 as i32)))?;
        manager.ResetDevice(&device, token).step("ResetDevice")?;

        let mut attributes: Option<IMFAttributes> = None;
        MFCreateAttributes(&mut attributes, 3).step("MFCreateAttributes")?;
        let attributes = attributes.ok_or(Step("MFCreateAttributes", HRESULT(0x8000_4003u32 as i32)))?;
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
                        let _ = writeln!(report, "GetService(MF_MEDIASOURCE_SERVICE): {}", if service.is_ok() { "ok" } else { "fails" });
                        service.step("GetService MF_MEDIASOURCE_SERVICE")?
                    }
                };
                let extension: IMFMediaEngineExtension =
                    SourceExtension { source, shared: Arc::clone(shared) }.into();
                attributes.SetUnknown(&MF_MEDIA_ENGINE_EXTENSION, &extension).step("SetUnknown extension")?;
                BSTR::from("bale-mss:probe")
            }
        };
        attributes.SetUnknown(&MF_MEDIA_ENGINE_DXGI_MANAGER, &manager).step("SetUnknown DXGI manager")?;
        attributes.SetUnknown(&MF_MEDIA_ENGINE_CALLBACK, &notify).step("SetUnknown callback")?;
        attributes
            .SetUINT32(&MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM.0 as u32)
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
        let engine = factory.CreateInstance(0, &attributes).step("CreateInstance")?;
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
        let _ = writeln!(report, "Open: {state} after {} ms", open.elapsed().as_millis());
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
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut texture: Option<ID3D11Texture2D> = None;
        device.CreateTexture2D(&desc, None, Some(&mut texture)).step("CreateTexture2D")?;
        let texture = texture.ok_or(Step("CreateTexture2D", HRESULT(0x8000_4003u32 as i32)))?;

        let cpu_before = process_cpu();
        let start_position = engine.GetCurrentTime();
        engine.Play().step("Play")?;
        let wall = Instant::now();
        let limit = Duration::from_secs(u64::from(seconds));
        let (mut polls, mut ticks, mut frames, mut failures) = (0u64, 0u64, 0u64, 0u64);
        let mut first_failure = HRESULT(0);
        let (mut transfer_total, mut transfer_max) = (Duration::ZERO, Duration::ZERO);
        let target = RECT { left: 0, top: 0, right: width as i32, bottom: height as i32 };
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
                let result = engine.TransferVideoFrame(&texture, None, &target, Some(&border));
                let elapsed = started.elapsed();
                transfer_total += elapsed;
                transfer_max = transfer_max.max(elapsed);
                match result {
                    Ok(()) => frames += 1,
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

        let _ = writeln!(report, "Poll loop: {polls} polls ({:.0}/s; thread::sleep(1 ms))", polls as f64 / wall_seconds);
        let _ = write!(report, "Stream ticks: {ticks}; transferred frames: {frames}; transfer failures: {failures}");
        if failures > 0 {
            let _ = write!(report, " (first 0x{:08X})", first_failure.0 as u32);
        }
        report.push('\n');
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
            let _ = writeln!(report, "CPU: {:.3} cores (process)", (after - before) / wall_seconds);
        }
        append_statistics(report, &engine_ex);
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
            let _ = writeln!(report, "Engine error: code={code} extended=0x{extended:08X}");
        }
    }
}

fn append_events(report: &mut String, shared: &Shared) {
    let events = shared.events.lock().map(|events| events.clone()).unwrap_or_default();
    let listed: Vec<String> = events
        .iter()
        .filter(|(event, _)| !matches!(event, 2 | 18))
        .take(40)
        .map(|(event, ms)| format!("{}@{ms}", event_name(*event)))
        .collect();
    let _ = writeln!(report, "Events (progress/timeupdate omitted): {}", listed.join(" "));
    let requests = shared.extension_requests.lock().map(|requests| requests.clone()).unwrap_or_default();
    if !requests.is_empty() {
        let kinds: Vec<String> = requests.iter().map(|kind| kind.to_string()).collect();
        let _ = writeln!(report, "Extension requests (0=media source, 1=byte stream): {}", kinds.join(" "));
    }
    let _ = writeln!(
        report,
        "First frame ready: {}",
        if shared.first_frame.load(Ordering::Acquire) { "True" } else { "False" }
    );
}

/// Process kernel+user CPU time in seconds.
fn process_cpu() -> Option<f64> {
    let (mut creation, mut exit, mut kernel, mut user) =
        (FILETIME::default(), FILETIME::default(), FILETIME::default(), FILETIME::default());
    // SAFETY: GetCurrentProcess is a pseudo-handle; all out-pointers are valid locals.
    unsafe { GetProcessTimes(GetCurrentProcess(), &mut creation, &mut exit, &mut kernel, &mut user) }.ok()?;
    let ticks = |time: FILETIME| (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
    Some((ticks(kernel) + ticks(user)) as f64 / 10_000_000.0)
}

/// Formats like .NET "0.###": up to `places` decimals, trailing zeros trimmed.
fn trim(value: f64, places: usize) -> String {
    let text = format!("{value:.places$}");
    if text.contains('.') { text.trim_end_matches('0').trim_end_matches('.').to_string() } else { text }
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
