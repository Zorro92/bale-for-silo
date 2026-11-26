//! Debug IMFMediaEngine rendering-mode **session**: the probe's engine (`engine::open_engine` +
//! `engine::attach_present`) kept open and driven by the caller (play, pause, seek, volume, resize),
//! with live state and statistics, so the app can play a title through the engine with its own
//! controls. Every call takes the session lock; the engine is free-threaded, the lock keeps calls
//! ordered. Reports carry step names and HRESULTs only, never the URL.

use std::fmt::Write as _;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use windows::Win32::Foundation::RECT;
use windows::Win32::Media::MediaFoundation::{
    IMFMediaEngine, IMFMediaEngineEx, MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE, MFARGB,
    MFSTARTUP_FULL, MFShutdown, MFStartup,
};
use windows::core::HRESULT;

use crate::engine::{
    Input, MF_VERSION, Opened, Present, Shared, Step, StepResult, append_error, append_events,
    attach_present, open_engine, stat_count,
};
use crate::engine_target::Target;

/// Live state for the caller, filled by `state`. Plain C layout.
#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct SessionState {
    pub position: f64,
    pub duration: f64,
    pub frames_per_second: f64,
    pub jitter: f64,
    pub frame_delay: f64,
    pub frames_rendered: u64,
    pub frames_dropped: u64,
    pub frames_corrupted: u64,
    pub width: u32,
    pub height: u32,
    /// Bit 0 paused, 1 ended, 2 error, 3 seeking, 4 waiting for data (ready state below 3).
    pub flags: u32,
    pub ready_state: u32,
}

/// One stream of the engine's media resource, filled by `stream_info`. Plain C layout; strings are
/// NUL-terminated UTF-16, cut to fit.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct StreamInfo {
    /// 0 unknown, 1 audio, 2 video, 3 any other major type.
    pub kind: u32,
    pub selected: u32,
    /// Audio channel count, 0 when unknown or not audio.
    pub channels: u32,
    /// MF_MT_SUBTYPE as GUID bytes (Data1/2/3 little-endian, then Data4), zero when unknown.
    pub subtype: [u8; 16],
    /// MF_SD_LANGUAGE (an RFC 1766 tag), empty when absent.
    pub language: [u16; 32],
    /// MF_SD_STREAM_NAME, empty when absent.
    pub name: [u16; 128],
}

impl Default for StreamInfo {
    fn default() -> Self {
        Self {
            kind: 0,
            selected: 0,
            channels: 0,
            subtype: [0; 16],
            language: [0; 32],
            name: [0; 128],
        }
    }
}

struct Inner {
    engine: IMFMediaEngine,
    engine_ex: IMFMediaEngineEx,
    shared: Arc<Shared>,
    _device: Option<Arc<crate::lifecycle::Device>>,
    width: u32,
    height: u32,
    closed: bool,
}

pub struct Session {
    inner: Mutex<Inner>,
    // Keeps a caller media source (and its engine extension's reference) alive with the engine.
    _input_keepalive: Option<windows::core::IUnknown>,
}

fn border() -> MFARGB {
    MFARGB {
        rgbBlue: 0,
        rgbGreen: 0,
        rgbRed: 0,
        rgbAlpha: 255,
    }
}

impl Session {
    /// Opens `input` in rendering mode, attaches it through `present`, seeks to `start` (seconds,
    /// when above zero) and starts playing unless `paused`. Returns the session or `None`, with the
    /// report either way.
    pub fn open(
        input: Input,
        target: Target,
        present: &Present,
        start: f64,
        paused: bool,
        muted: bool,
    ) -> (Option<Box<Session>>, String) {
        let mut report = String::from(
            "Engine session (Rust bale_gpu.dll; rendering mode: windowless swap chain on a SwapChainPanel)\n",
        );
        if target != Target::Bgra {
            // bale_gpu_engine_session_open_target (per mount; bit-depth rule) or the old setter.
            let _ = writeln!(
                report,
                "Target: {} (DXGI_FORMAT_{}) as MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT",
                target.name(),
                target.format_name()
            );
        }
        // SAFETY: plain MF startup, paired with MFShutdown on failure here or in `close`.
        if let Err(error) = unsafe { MFStartup(MF_VERSION, MFSTARTUP_FULL) } {
            let _ = writeln!(
                report,
                "Error: 0x{:08X} at MFStartup",
                error.code().0 as u32
            );
            return (None, report);
        }
        let shared = Shared::new();
        let mut engine_slot = None;
        let mut device = None;
        let keepalive = match &input {
            Input::Source(object) | Input::Stream { stream: object, .. } => Some(object.clone()),
            Input::Url(_) => None,
        };
        // SAFETY: COM objects are created on this thread; the engine is free-threaded afterwards.
        let opened = unsafe {
            open_engine(
                &input,
                target,
                true,
                muted,
                &shared,
                &mut engine_slot,
                &mut device,
                &mut report,
            )
        };
        let result = match opened {
            Ok(Some(Opened {
                engine,
                engine_ex,
                width,
                height,
                ..
            })) => {
                // SAFETY: live engine interfaces; `present` is valid for this call.
                let attached = unsafe {
                    attach_present(&engine, &engine_ex, present, &mut report).and_then(|()| {
                        if start > 0.0 {
                            engine
                                .SetCurrentTime(start)
                                .step("SetCurrentTime (start)")?;
                        }
                        if paused {
                            engine.Pause().step("Pause")
                        } else {
                            engine.Play().step("Play")
                        }
                    })
                };
                match attached {
                    Ok(()) => Ok((engine, engine_ex, width, height)),
                    Err(step) => Err(step),
                }
            }
            Ok(None) => Err(Step(
                "open (no canplay or no video)",
                HRESULT(0x8000_4005u32 as i32),
            )),
            Err(step) => Err(step),
        };
        match result {
            Ok((engine, engine_ex, width, height)) => {
                let _ = writeln!(report, "Session: open, {width}x{height}, start {start:.3}s");
                let session = Box::new(Session {
                    inner: Mutex::new(Inner {
                        engine,
                        engine_ex,
                        shared,
                        _device: device,
                        width: present.width,
                        height: present.height,
                        closed: false,
                    }),
                    _input_keepalive: keepalive,
                });
                (Some(session), report)
            }
            Err(Step(name, hr)) => {
                let _ = writeln!(report, "Error: 0x{:08X} at {name}", hr.0 as u32);
                if let Some(engine) = engine_slot.take() {
                    // SAFETY: the engine is alive; Shutdown precedes its release.
                    unsafe {
                        append_error(&mut report, &engine);
                        let _ = engine.Shutdown();
                    }
                }
                append_events(&mut report, &shared);
                drop(device);
                drop(keepalive);
                // SAFETY: every MF object created after MFStartup is released above.
                unsafe {
                    let _ = MFShutdown();
                }
                (None, report)
            }
        }
    }

    fn with<T>(
        &self,
        f: impl FnOnce(&mut Inner) -> windows::core::Result<T>,
    ) -> windows::core::Result<T> {
        let mut inner = self
            .inner
            .lock()
            .map_err(|_| windows::core::Error::from_hresult(HRESULT(0x8000_FFFFu32 as i32)))?;
        if inner.closed {
            return Err(windows::core::Error::from_hresult(HRESULT(
                0x8000_000Eu32 as i32,
            )));
        }
        f(&mut inner)
    }

    pub fn play(&self) -> HRESULT {
        // SAFETY: live engine.
        hr(self.with(|inner| unsafe { inner.engine.Play() }))
    }

    pub fn pause(&self) -> HRESULT {
        // SAFETY: live engine.
        hr(self.with(|inner| unsafe { inner.engine.Pause() }))
    }

    /// Seeks to `seconds`; `approximate` lets the engine land on a nearby keyframe
    /// (`SetCurrentTimeEx` with `MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE`) instead of decoding up to the
    /// exact time, which a caller media source may or may not honour.
    pub fn seek(&self, seconds: f64, approximate: bool) -> HRESULT {
        if !seconds.is_finite() || seconds < 0.0 {
            return HRESULT(0x8007_0057u32 as i32); // E_INVALIDARG
        }
        // SAFETY: live engine.
        hr(self.with(|inner| unsafe {
            if approximate {
                inner
                    .engine_ex
                    .SetCurrentTimeEx(seconds, MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE)
            } else {
                inner.engine.SetCurrentTime(seconds)
            }
        }))
    }

    pub fn set_volume(&self, volume: f64, muted: bool) -> HRESULT {
        // SAFETY: live engine.
        hr(self.with(|inner| unsafe {
            inner.engine.SetVolume(volume.clamp(0.0, 1.0))?;
            inner.engine.SetMuted(muted)
        }))
    }

    pub fn resize(&self, width: u32, height: u32) -> HRESULT {
        if width == 0 || height == 0 {
            return HRESULT(0x8007_0057u32 as i32);
        }
        hr(self.with(|inner| {
            if (inner.width, inner.height) == (width, height) {
                return Ok(());
            }
            let rect = RECT {
                left: 0,
                top: 0,
                right: width as i32,
                bottom: height as i32,
            };
            // SAFETY: live engine; locals outlive the call.
            unsafe {
                inner
                    .engine_ex
                    .UpdateVideoStream(None, Some(&rect), Some(&border()))?;
            }
            inner.width = width;
            inner.height = height;
            Ok(())
        }))
    }

    pub fn state(&self, out: &mut SessionState) -> HRESULT {
        hr(self.with(|inner| {
            // SAFETY: live engine; GetStatistics owns nothing for numeric variants.
            unsafe {
                let engine = &inner.engine;
                let (mut width, mut height) = (0u32, 0u32);
                let _ = engine.GetNativeVideoSize(Some(&mut width), Some(&mut height));
                let ready = u32::from(engine.GetReadyState());
                let mut flags = 0u32;
                if engine.IsPaused().as_bool() {
                    flags |= 1;
                }
                if engine.IsEnded().as_bool() || inner.shared.ended() {
                    flags |= 2;
                }
                if inner.shared.failed() {
                    flags |= 4;
                }
                if engine.IsSeeking().as_bool() {
                    flags |= 8;
                }
                if ready < 3 {
                    flags |= 16;
                }
                *out = SessionState {
                    position: engine.GetCurrentTime(),
                    duration: engine.GetDuration(),
                    frames_per_second: stat_float(&inner.engine_ex, 4),
                    jitter: stat_float(&inner.engine_ex, 5),
                    frame_delay: stat_float(&inner.engine_ex, 7),
                    frames_rendered: stat_count(&inner.engine_ex, 0).unwrap_or(0),
                    frames_dropped: stat_count(&inner.engine_ex, 1).unwrap_or(0),
                    frames_corrupted: stat_count(&inner.engine_ex, 6).unwrap_or(0),
                    width,
                    height,
                    flags,
                    ready_state: ready,
                };
            }
            Ok(())
        }))
    }

    pub fn playback_events(
        &self,
        out: &mut [crate::engine_events::PlaybackEvent],
        count: &mut u32,
        seconds: &mut f64,
        lost: &mut u32,
    ) -> HRESULT {
        hr(self.with(|inner| {
            if let Ok(mut events) = inner.shared.playback_events.lock() {
                *count = events.drain(out) as u32;
                *seconds = inner.shared.elapsed_seconds();
                *lost = events.lost;
            }
            Ok(())
        }))
    }

    /// The engine's clock for a debug frame observer (`bale_frame_observer_attach_engine`): fills
    /// `Shared::engine_clock`, which `close` empties again, and returns the state that holds it.
    pub(crate) fn frame_clock(&self) -> windows::core::Result<Arc<Shared>> {
        self.with(|inner| {
            inner.shared.engine_clock.set(Some(&inner.engine));
            Ok(Arc::clone(&inner.shared))
        })
    }

    /// Debug frame log on or off: while on, each playback event also records the engine position at the
    /// event, read in the engine's callback through `Shared::engine_clock`. Debug probes only: production
    /// sessions keep it off.
    #[cfg(feature = "debug-probes")]
    pub fn set_frame_log(&self, on: bool) -> HRESULT {
        hr(self.with(|inner| {
            if on {
                inner.shared.engine_clock.set(Some(&inner.engine));
            }
            inner.shared.frame_log.store(on, Ordering::Release);
            Ok(())
        }))
    }

    pub fn stream_count(&self, out: &mut u32) -> HRESULT {
        // SAFETY: live engine.
        hr(self.with(|inner| unsafe {
            *out = inner.engine_ex.GetNumberOfStreams()?;
            Ok(())
        }))
    }

    /// Describes stream `index` (major type, selection, channels, subtype, language, name).
    pub fn stream_info(&self, index: u32, out: &mut StreamInfo) -> HRESULT {
        use windows::Win32::Media::MediaFoundation::{
            MF_MT_AUDIO_NUM_CHANNELS, MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE, MF_SD_LANGUAGE,
            MF_SD_STREAM_NAME, MFMediaType_Audio, MFMediaType_Video,
        };
        hr(self.with(|inner| {
            let engine = &inner.engine_ex;
            let mut info = StreamInfo::default();
            // SAFETY: live engine; every PROPVARIANT read is cleared in `attribute`.
            unsafe {
                if index >= engine.GetNumberOfStreams()? {
                    return Err(windows::core::Error::from_hresult(HRESULT(
                        0x8007_0057u32 as i32,
                    )));
                }
                info.selected = u32::from(engine.GetStreamSelection(index)?.as_bool());
                if let Some(Value::Guid(major)) = attribute(engine, index, &MF_MT_MAJOR_TYPE) {
                    info.kind = if major == MFMediaType_Audio {
                        1
                    } else if major == MFMediaType_Video {
                        2
                    } else {
                        3
                    };
                }
                if let Some(Value::Guid(subtype)) = attribute(engine, index, &MF_MT_SUBTYPE) {
                    info.subtype = guid_bytes(&subtype);
                }
                if let Some(Value::Number(channels)) =
                    attribute(engine, index, &MF_MT_AUDIO_NUM_CHANNELS)
                {
                    info.channels = channels;
                }
                if let Some(Value::Text(language)) = attribute(engine, index, &MF_SD_LANGUAGE) {
                    copy_text(&language, &mut info.language);
                }
                if let Some(Value::Text(name)) = attribute(engine, index, &MF_SD_STREAM_NAME) {
                    copy_text(&name, &mut info.name);
                }
            }
            *out = info;
            Ok(())
        }))
    }

    /// Selects stream `index` and deselects every other stream of its major type (one audio
    /// track, one video track), then applies the selection.
    pub fn select_stream(&self, index: u32) -> HRESULT {
        use windows::Win32::Media::MediaFoundation::MF_MT_MAJOR_TYPE;
        hr(self.with(|inner| {
            let engine = &inner.engine_ex;
            // SAFETY: live engine.
            unsafe {
                let count = engine.GetNumberOfStreams()?;
                if index >= count {
                    return Err(windows::core::Error::from_hresult(HRESULT(
                        0x8007_0057u32 as i32,
                    )));
                }
                let major = |i| match attribute(engine, i, &MF_MT_MAJOR_TYPE) {
                    Some(Value::Guid(guid)) => Some(guid),
                    _ => None,
                };
                let wanted = major(index);
                for i in 0..count {
                    if i == index {
                        engine.SetStreamSelection(i, true)?;
                    } else if wanted.is_some() && major(i) == wanted {
                        engine.SetStreamSelection(i, false)?;
                    }
                }
                engine.ApplyStreamSelections()
            }
        }))
    }

    /// Shuts the engine down and returns the closing report (engine error, events).
    pub fn close(&self) -> String {
        let mut report = String::new();
        if let Ok(mut inner) = self.inner.lock()
            && !inner.closed
        {
            inner.closed = true;
            // No debug clock reader may be inside the engine during or after Shutdown (EngineClock); this also
            // ends the reference cycle a filled clock makes.
            inner.shared.frame_log.store(false, Ordering::Release);
            inner.shared.engine_clock.set(None);
            // SAFETY: the engine is alive; Shutdown precedes its final release (on drop).
            unsafe {
                append_error(&mut report, &inner.engine);
                let _ = inner.engine.Shutdown();
            }
            append_events(&mut report, &inner.shared);
        }
        report
    }
}

/// Owner of a session plus the MF startup it made; dropping it releases the engine, then MF.
pub struct SessionHandle(pub Option<Box<Session>>);

impl Drop for SessionHandle {
    fn drop(&mut self) {
        if let Some(session) = self.0.take() {
            let _ = session.close();
            drop(session);
            // SAFETY: the session's MF objects were released just above.
            unsafe {
                let _ = MFShutdown();
            }
        }
    }
}

/// One engine statistic as a float (fps, jitter, delay).
fn stat_float(engine_ex: &IMFMediaEngineEx, id: i32) -> f64 {
    use windows::Win32::Media::MediaFoundation::MF_MEDIA_ENGINE_STATISTIC;
    // SAFETY: GetStatistics fills a PROPVARIANT; numeric variants own nothing.
    unsafe {
        let Ok(value) = engine_ex.GetStatistics(MF_MEDIA_ENGINE_STATISTIC(id)) else {
            return f64::NAN;
        };
        let inner = &value.Anonymous.Anonymous;
        match inner.vt.0 {
            4 => f64::from(inner.Anonymous.fltVal),
            5 => inner.Anonymous.dblVal,
            3 | 19 => f64::from(inner.Anonymous.ulVal),
            20 | 21 => inner.Anonymous.uhVal as f64,
            _ => f64::NAN,
        }
    }
}

enum Value {
    Guid(windows::core::GUID),
    Number(u32),
    Text(Vec<u16>),
}

/// One stream attribute (stream descriptor or media type) as a GUID, a 32-bit number or text; the
/// PROPVARIANT is always cleared.
unsafe fn attribute(
    engine: &IMFMediaEngineEx,
    index: u32,
    key: &windows::core::GUID,
) -> Option<Value> {
    use windows::Win32::System::Com::StructuredStorage::PropVariantClear;
    // SAFETY: GetStreamAttribute fills a PROPVARIANT we own and clear below; pointers are read
    // before the clear.
    unsafe {
        let mut variant = engine.GetStreamAttribute(index, key).ok()?;
        let inner = &variant.Anonymous.Anonymous;
        let value = match inner.vt.0 {
            72 if !inner.Anonymous.puuid.is_null() => Some(Value::Guid(*inner.Anonymous.puuid)),
            19 | 3 => Some(Value::Number(inner.Anonymous.ulVal)),
            31 if !inner.Anonymous.pwszVal.is_null() => {
                Some(Value::Text(inner.Anonymous.pwszVal.as_wide().to_vec()))
            }
            _ => None,
        };
        let _ = PropVariantClear(&mut variant);
        value
    }
}

fn guid_bytes(guid: &windows::core::GUID) -> [u8; 16] {
    let mut bytes = [0u8; 16];
    bytes[0..4].copy_from_slice(&guid.data1.to_le_bytes());
    bytes[4..6].copy_from_slice(&guid.data2.to_le_bytes());
    bytes[6..8].copy_from_slice(&guid.data3.to_le_bytes());
    bytes[8..16].copy_from_slice(&guid.data4);
    bytes
}

/// Copies `text` into `out` as NUL-terminated UTF-16, cut to fit.
fn copy_text(text: &[u16], out: &mut [u16]) {
    let length = text.len().min(out.len().saturating_sub(1));
    out[..length].copy_from_slice(&text[..length]);
    out[length] = 0;
}

fn hr(result: windows::core::Result<()>) -> HRESULT {
    match result {
        Ok(()) => HRESULT(0),
        Err(error) => error.code(),
    }
}
