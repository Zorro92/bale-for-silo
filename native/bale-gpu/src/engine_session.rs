//! Debug IMFMediaEngine rendering-mode **session**: the probe's engine (`engine::open_engine` +
//! `engine::attach_present`) kept open and driven by the caller (play, pause, seek, volume, resize),
//! with live state and statistics, so the app can play a title through the engine with its own
//! controls. Every call takes the session lock; the engine is free-threaded, the lock keeps calls
//! ordered. Reports carry step names and HRESULTs only, never the URL.

use std::fmt::Write as _;
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

    /// Shuts the engine down and returns the closing report (engine error, events).
    pub fn close(&self) -> String {
        let mut report = String::new();
        if let Ok(mut inner) = self.inner.lock()
            && !inner.closed
        {
            inner.closed = true;
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

fn hr(result: windows::core::Result<()>) -> HRESULT {
    match result {
        Ok(()) => HRESULT(0),
        Err(error) => error.code(),
    }
}
