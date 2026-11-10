//! The subtitle overlay's Direct3D 11 renderer and render thread.
//!
//! One thread per overlay owns the libass session, the immediate context and the swap chain. API
//! calls only update [`State`] and wake that thread, so libass work (a first-use font lookup takes
//! 15-65 ms on the console) never runs on the caller, which is the UI thread. The one exception is
//! `load`, which waits for the parse result because the ABI reports unparsable scripts.
//!
//! Subtitle text and URLs are never logged; nothing here logs at all.

use std::collections::VecDeque;
use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{CloseHandle, E_NOTIMPL, HANDLE, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_0,
    D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP,
};
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_ALPHA_MODE_PREMULTIPLIED, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8_UNORM,
    DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT_R32G32B32A32_FLOAT, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_MATRIX_3X2_F, DXGI_PRESENT, DXGI_SCALING_STRETCH, DXGI_SWAP_CHAIN_DESC1,
    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT, DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
    DXGI_USAGE_RENDER_TARGET_OUTPUT, IDXGIDevice, IDXGIFactory2, IDXGISwapChain2,
};
use windows::Win32::System::Threading::WaitForSingleObject;
use windows::core::{Error, IUnknown, Interface, PCSTR, Result};

use super::{
    ATLAS_START, BaleSubStats, Clock, E_FAIL, E_INVALIDARG, Layout, S_OK, Shelves, atlas_size_for,
    premultiplied,
};
use crate::ass::{Image, Session, Track};

const VERTEX_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/sub_vs.cso"));
const PIXEL_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/sub_ps.cso"));

const BUFFER_COUNT: u32 = 2;
/// While playing with nothing to present there is no frame-latency signal to wait for, so the
/// thread asks libass again after this long (a cached, unchanged render is cheap).
const POLL: Duration = Duration::from_millis(8);
/// Upper bound for one frame-latency wait, so a swap chain nobody composites cannot hang the thread.
const LATENCY_TIMEOUT_MS: u32 = 1000;

mod panel {
    #![allow(non_snake_case)]
    use windows_core::{HRESULT, IUnknown, IUnknown_Vtbl, interface};

    /// `ISwapChainPanelNative` of Windows.UI.Xaml (windows.ui.xaml.media.dxinterop.h); the
    /// `windows` crate no longer ships the XAML interop interfaces.
    #[interface("F92F19D2-3ADE-45A6-A20C-F6F1EA90554B")]
    pub unsafe trait ISwapChainPanelNative: IUnknown {
        pub unsafe fn SetSwapChain(&self, swap_chain: *mut core::ffi::c_void) -> HRESULT;
    }
}
use panel::ISwapChainPanelNative;

/// One quad: matches `Input` in `shaders/sub_vs.hlsl`.
#[repr(C)]
#[derive(Clone, Copy)]
struct Instance {
    /// Destination x, y, width, height in frame pixels.
    rect: [f32; 4],
    /// Top-left texel of the bitmap in the atlas.
    atlas: [f32; 2],
    /// Premultiplied RGBA.
    color: [f32; 4],
}

enum Command {
    Load(Vec<u8>),
    Clear,
}

/// Everything API calls and the render thread share.
struct State {
    /// Set with every change; the render thread clears it when it takes the work.
    signal: bool,
    stop: bool,
    /// The render thread is gone (stopped or panicked).
    exited: bool,
    layout: Option<Layout>,
    commands: VecDeque<Command>,
    /// Fonts added since the last load, handed to libass by the next one.
    fonts: Vec<(String, Vec<u8>)>,
    clock: Clock,
    clock_stamp: Instant,
    capture: bool,
    load_result: Option<i32>,
    capture_result: Option<Option<Capture>>,
    /// First Direct3D/DXGI failure of the render thread; drawing has stopped.
    failure: Option<i32>,
    stats: BaleSubStats,
}

struct Shared {
    state: Mutex<State>,
    /// Wakes the render thread.
    wake: Condvar,
    /// Wakes callers waiting for a load or capture result.
    done: Condvar,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Applies `change` and wakes the render thread; the render thread's first failure (or
    /// `E_FAIL` once it is gone) is returned instead of applying anything.
    fn post(&self, change: impl FnOnce(&mut State)) -> i32 {
        let mut state = self.lock();
        if let Some(hr) = state.unusable() {
            return hr;
        }
        change(&mut state);
        state.signal = true;
        self.wake.notify_one();
        S_OK
    }
}

impl State {
    fn unusable(&self) -> Option<i32> {
        if self.exited {
            Some(E_FAIL)
        } else {
            self.failure
        }
    }
}

/// A read-back frame (tests): premultiplied BGRA rows, top to bottom.
pub struct Capture {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
}

/// The handle behind the `bale_sub_overlay_*` exports.
pub struct Overlay {
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
    /// Only touched by `create` and `Drop`, both on the UI thread.
    panel: Option<ISwapChainPanelNative>,
    /// One load (and one capture) waits for its result at a time.
    load_lock: Mutex<()>,
    capture_lock: Mutex<()>,
}

impl Overlay {
    /// Creates the device, the composition swap chain and the render thread. `panel` is a borrowed
    /// XAML `SwapChainPanel` (`IInspectable*`) or null to render offscreen; when it is set this
    /// must run on the UI thread.
    ///
    /// # Safety
    /// `panel` must be null or a live COM pointer for the call.
    pub unsafe fn create(panel: *mut c_void, width: u32, height: u32) -> Result<Box<Self>> {
        // SAFETY: forwarded contract.
        unsafe { Self::create_with_driver(panel, width, height, D3D_DRIVER_TYPE_HARDWARE) }
    }

    /// [`Overlay::create`] on a chosen driver type (tests use WARP).
    ///
    /// # Safety
    /// As [`Overlay::create`].
    pub unsafe fn create_with_driver(
        panel: *mut c_void,
        width: u32,
        height: u32,
        driver: D3D_DRIVER_TYPE,
    ) -> Result<Box<Self>> {
        let layout = Layout::full(width, height);
        if !layout.is_valid() {
            return Err(Error::from_hresult(windows::core::HRESULT(E_INVALIDARG)));
        }
        let gpu = Gpu::new(driver, width, height)?;
        // SAFETY: the caller guarantees a live COM pointer when non-null.
        let panel = match unsafe { IUnknown::from_raw_borrowed(&panel) } {
            Some(panel) => {
                let native: ISwapChainPanelNative = panel.cast()?;
                // SAFETY: live interfaces; the panel takes its own swap chain reference.
                unsafe { native.SetSwapChain(gpu.swap_chain.as_raw()) }.ok()?;
                Some(native)
            }
            None => None,
        };
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                signal: false,
                stop: false,
                exited: false,
                layout: None,
                commands: VecDeque::new(),
                fonts: Vec::new(),
                clock: Clock {
                    position_ms: 0,
                    rate: 1.0,
                    playing: false,
                },
                clock_stamp: Instant::now(),
                capture: false,
                load_result: None,
                capture_result: None,
                failure: None,
                stats: BaleSubStats {
                    width,
                    height,
                    ..BaleSubStats::default()
                },
            }),
            wake: Condvar::new(),
            done: Condvar::new(),
        });
        let thread = {
            let shared = Arc::clone(&shared);
            std::thread::Builder::new()
                .name("bale-subs".into())
                .spawn(move || {
                    let _exit = Exit(&shared);
                    // A panic (ours or in a dependency) ends the thread; `Exit` then fails callers.
                    let _ = catch_unwind(AssertUnwindSafe(|| run(&shared, gpu, layout)));
                })
        };
        let detach = |panel: &Option<ISwapChainPanelNative>| {
            if let Some(panel) = panel {
                // SAFETY: live interface; null detaches the swap chain.
                let _ = unsafe { panel.SetSwapChain(std::ptr::null_mut()) };
            }
        };
        let thread = match thread {
            Ok(thread) => thread,
            Err(_) => {
                detach(&panel);
                return Err(Error::from_hresult(windows::core::HRESULT(E_FAIL)));
            }
        };
        Ok(Box::new(Overlay {
            shared,
            thread: Some(thread),
            panel,
            load_lock: Mutex::new(()),
            capture_lock: Mutex::new(()),
        }))
    }

    pub fn set_layout(&self, layout: Layout) -> i32 {
        if !layout.is_valid() {
            return E_INVALIDARG;
        }
        self.shared.post(|state| state.layout = Some(layout))
    }

    /// Replaces the track. Waits for the render thread to parse `script`: `E_INVALIDARG` when
    /// libass does not recognise it (the previous track is dropped either way).
    pub fn load(&self, script: &[u8]) -> i32 {
        if script.is_empty() {
            return E_INVALIDARG;
        }
        let _one = self
            .load_lock
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let script = script.to_vec();
        let posted = self.shared.post(|state| {
            state.load_result = None;
            state.commands.push_back(Command::Load(script));
        });
        if posted != S_OK {
            return posted;
        }
        let mut state = self.shared.lock();
        loop {
            if let Some(hr) = state.load_result.take() {
                return hr;
            }
            if state.exited {
                return E_FAIL;
            }
            state = self
                .shared
                .done
                .wait(state)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }

    pub fn add_font(&self, name: &[u8], data: &[u8]) -> i32 {
        if data.is_empty() || i32::try_from(data.len()).is_err() {
            return E_INVALIDARG;
        }
        let font = (String::from_utf8_lossy(name).into_owned(), data.to_vec());
        self.shared.post(|state| state.fonts.push(font))
    }

    pub fn clear(&self) -> i32 {
        self.shared.post(|state| {
            if !matches!(state.commands.back(), Some(Command::Clear)) {
                state.commands.push_back(Command::Clear);
            }
        })
    }

    pub fn set_clock(&self, position_ms: i64, rate: f64, playing: bool) -> i32 {
        if !rate.is_finite() {
            return E_INVALIDARG;
        }
        let stamp = Instant::now();
        self.shared.post(|state| {
            state.clock = Clock {
                position_ms,
                rate,
                playing,
            };
            state.clock_stamp = stamp;
        })
    }

    pub fn stats(&self) -> BaleSubStats {
        self.shared.lock().stats
    }

    /// Redraws the current frame, reads the back buffer and presents it (tests; not in the C ABI).
    /// `None` when the overlay cannot draw.
    pub fn capture(&self) -> Option<Capture> {
        let _one = self
            .capture_lock
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let posted = self.shared.post(|state| {
            state.capture_result = None;
            state.capture = true;
        });
        if posted != S_OK {
            return None;
        }
        let mut state = self.shared.lock();
        loop {
            if let Some(capture) = state.capture_result.take() {
                return capture;
            }
            if state.exited {
                return None;
            }
            state = self
                .shared
                .done
                .wait(state)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }
}

impl Drop for Overlay {
    fn drop(&mut self) {
        {
            let mut state = self.shared.lock();
            state.stop = true;
            state.signal = true;
            self.shared.wake.notify_one();
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
        if let Some(panel) = self.panel.take() {
            // SAFETY: live interface, on the UI thread (destroy's contract); null detaches.
            let _ = unsafe { panel.SetSwapChain(std::ptr::null_mut()) };
        }
    }
}

/// Marks the render thread as gone and releases every waiting caller, however the thread ends.
struct Exit<'a>(&'a Shared);

impl Drop for Exit<'_> {
    fn drop(&mut self) {
        let mut state = self.0.lock();
        state.exited = true;
        self.0.done.notify_all();
    }
}

struct Work {
    stop: bool,
    layout: Option<Layout>,
    commands: VecDeque<Command>,
    fonts: Vec<(String, Vec<u8>)>,
    clock: Clock,
    clock_stamp: Instant,
    capture: bool,
}

/// How the render thread waits for its next iteration.
#[derive(Clone, Copy, PartialEq)]
enum Pace {
    /// Until an API call: nothing can change on its own (no track, paused, or drawing failed).
    Idle,
    /// A frame was presented; the frame-latency wait already paced the loop.
    Presented,
    /// Playing, nothing presented: ask libass again after [`POLL`].
    Poll,
}

fn take_work(shared: &Shared, pace: Pace) -> Work {
    let mut state = shared.lock();
    match pace {
        Pace::Idle => {
            while !state.signal {
                state = shared
                    .wake
                    .wait(state)
                    .unwrap_or_else(PoisonError::into_inner);
            }
        }
        Pace::Poll if !state.signal => {
            state = shared
                .wake
                .wait_timeout(state, POLL)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
        Pace::Poll | Pace::Presented => {}
    }
    state.signal = false;
    Work {
        stop: state.stop,
        layout: state.layout.take(),
        commands: std::mem::take(&mut state.commands),
        fonts: std::mem::take(&mut state.fonts),
        clock: state.clock,
        clock_stamp: state.clock_stamp,
        capture: std::mem::take(&mut state.capture),
    }
}

/// The render thread: applies queued changes, renders the track at the media clock and presents
/// only frames that differ from what is on screen.
fn run(shared: &Shared, mut gpu: Gpu, mut layout: Layout) {
    let apply_layout = |session: &Session, layout: &Layout| {
        session.set_layout(
            layout.width as i32,
            layout.height as i32,
            layout.margins(),
            layout.storage_size(),
        );
    };
    // Without a session every load fails; clear, layout and capture still work.
    let session = Session::new(layout.width as i32, layout.height as i32, &[]).ok();
    if let Some(session) = &session {
        apply_layout(session, &layout);
    }
    let mut track: Option<Track<'_>> = None;
    let mut fonts: Vec<(String, Vec<u8>)> = Vec::new();
    let mut stats = BaleSubStats {
        width: layout.width,
        height: layout.height,
        ..BaleSubStats::default()
    };
    let mut failure: Option<i32> = None;
    let mut on_screen = false;
    // The waitable object starts signalled once; consuming that keeps later waits one per present.
    let mut latency_owed = true;
    let mut pace = Pace::Idle;
    loop {
        if latency_owed && pace == Pace::Presented {
            gpu.wait_for_latency();
            latency_owed = false;
        }
        let mut work = take_work(shared, pace);
        if work.stop {
            break;
        }
        fonts.append(&mut work.fonts);
        // Layout, track and capture changes redraw even when libass reports no change.
        let mut force = work.capture;
        if let Some(new) = work.layout {
            layout = new;
            if failure.is_none()
                && let Err(error) = gpu.set_layout(&layout)
            {
                failure = Some(error.code().0);
            }
            if let Some(session) = &session {
                apply_layout(session, &layout);
            }
            stats.width = layout.width;
            stats.height = layout.height;
            force = true;
        }
        for command in work.commands {
            // The old track goes first either way: stale subtitles are worse than none.
            track = None;
            stats.events = 0;
            force = true;
            let Command::Load(script) = command else {
                continue;
            };
            let result = match &session {
                None => E_FAIL,
                Some(session) => {
                    for (name, bytes) in fonts.drain(..) {
                        session.add_font(&name, &bytes);
                    }
                    match session.track(&script) {
                        Some(parsed) => {
                            stats.events = parsed.events();
                            track = Some(parsed);
                            S_OK
                        }
                        None => E_INVALIDARG,
                    }
                }
            };
            stats.max_render_us = 0;
            // Answer the waiting caller before the font provider (re)starts below.
            let mut state = shared.lock();
            state.load_result = Some(result);
            shared.done.notify_all();
        }

        let mut presented = false;
        let mut capture = None;
        if failure.is_none() {
            let now = work.clock.at(work.clock_stamp.elapsed());
            let drawn = match (&session, &track) {
                (Some(session), Some(track)) => {
                    session.ensure_fonts();
                    stats.renders += 1;
                    gpu.draw_track(session, track, now, force)
                }
                _ if force || on_screen => gpu.draw_empty().map(|()| Some(Drawn::default())),
                _ => Ok(None),
            };
            let frame = drawn.and_then(|drawn| {
                let Some(drawn) = drawn else {
                    return Ok(None);
                };
                if work.capture {
                    capture = Some(gpu.read_back()?);
                }
                gpu.present()?;
                Ok(Some(drawn))
            });
            match frame {
                Ok(Some(drawn)) => {
                    presented = true;
                    latency_owed = true;
                    on_screen = drawn.images > 0;
                    stats.presents += 1;
                    if track.is_some() {
                        let micros = u32::try_from(drawn.elapsed.as_micros()).unwrap_or(u32::MAX);
                        stats.last_render_us = micros;
                        stats.max_render_us = stats.max_render_us.max(micros);
                        stats.images = drawn.images;
                    }
                }
                Ok(None) => {}
                Err(error) => {
                    failure = Some(error.code().0);
                    capture = None;
                }
            }
        }

        let mut state = shared.lock();
        state.stats = stats;
        state.failure = failure;
        if work.capture {
            state.capture_result = Some(capture);
            shared.done.notify_all();
        }
        drop(state);
        pace = if failure.is_some() || track.is_none() || !work.clock.playing {
            Pace::Idle
        } else if presented {
            Pace::Presented
        } else {
            Pace::Poll
        };
    }
}

#[derive(Default)]
struct Drawn {
    images: u32,
    /// libass render + upload + draw (not the present).
    elapsed: Duration,
}

struct Atlas {
    texture: ID3D11Texture2D,
    view: ID3D11ShaderResourceView,
    width: u32,
    height: u32,
}

impl Atlas {
    fn new(device: &ID3D11Device, (width, height): (u32, u32)) -> Result<Self> {
        let mut texture = None;
        let mut view = None;
        // SAFETY: valid descriptor and live output slots; no initial data.
        unsafe {
            device.CreateTexture2D(
                &D3D11_TEXTURE2D_DESC {
                    Width: width,
                    Height: height,
                    MipLevels: 1,
                    ArraySize: 1,
                    Format: DXGI_FORMAT_R8_UNORM,
                    SampleDesc: DXGI_SAMPLE_DESC {
                        Count: 1,
                        Quality: 0,
                    },
                    Usage: D3D11_USAGE_DYNAMIC,
                    BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
                    CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
                    MiscFlags: 0,
                },
                None,
                Some(&mut texture),
            )?;
            let texture: ID3D11Texture2D = texture.ok_or_else(fail)?;
            device.CreateShaderResourceView(&texture, None, Some(&mut view))?;
            Ok(Atlas {
                texture,
                view: view.ok_or_else(fail)?,
                width,
                height,
            })
        }
    }
}

fn fail() -> Error {
    Error::from_hresult(windows::core::HRESULT(E_FAIL))
}

/// Device, swap chain and the quad pipeline. Lives on the render thread after `create`.
struct Gpu {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    swap_chain: IDXGISwapChain2,
    /// Frame-latency waitable object (owned), or invalid when the swap chain gave none.
    latency: HANDLE,
    target: Option<ID3D11RenderTargetView>,
    vertex_shader: ID3D11VertexShader,
    pixel_shader: ID3D11PixelShader,
    input_layout: ID3D11InputLayout,
    blend: ID3D11BlendState,
    rasterizer: ID3D11RasterizerState,
    constants: ID3D11Buffer,
    atlas: Atlas,
    instance_buffer: Option<ID3D11Buffer>,
    instance_capacity: usize,
    width: u32,
    height: u32,
    // Per-frame batching state.
    /// The mapped atlas of the open batch: first byte and row pitch.
    mapped: Option<(*mut u8, usize)>,
    shelves: Shelves,
    pending: Vec<Instance>,
    /// Sizes of this frame's images, to size the atlas for the next frame when one batch was not enough.
    sizes: Vec<(u32, u32)>,
    batches: u32,
}

// SAFETY: `create` builds the value and hands it to the render thread, which is its only user from
// then on. Direct3D 11 devices and DXGI swap chains are free-threaded; the immediate context and the
// mapped pointer are only used by that one thread.
unsafe impl Send for Gpu {}

impl Drop for Gpu {
    fn drop(&mut self) {
        if !self.latency.is_invalid() {
            // SAFETY: the handle came from GetFrameLatencyWaitableObject and is closed once.
            let _ = unsafe { CloseHandle(self.latency) };
        }
    }
}

impl Gpu {
    fn new(driver: D3D_DRIVER_TYPE, width: u32, height: u32) -> Result<Self> {
        if cfg!(bale_grain_no_shaders) {
            return Err(E_NOTIMPL.into());
        }
        let mut device = None;
        let mut context = None;
        // SAFETY: no adapter or software module; out-pointers are live local COM slots.
        unsafe {
            D3D11CreateDevice(
                None,
                driver,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                Some(&[D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )?;
        }
        let device: ID3D11Device = device.ok_or_else(fail)?;
        let context: ID3D11DeviceContext = context.ok_or_else(fail)?;
        let flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: width,
            Height: height,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: BUFFER_COUNT,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            // Transparent where nothing is drawn, so the video shows through.
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags: flags.0 as u32,
        };
        // SAFETY: live device; the descriptor outlives the call.
        let swap_chain: IDXGISwapChain2 = unsafe {
            let factory: IDXGIFactory2 = device.cast::<IDXGIDevice>()?.GetAdapter()?.GetParent()?;
            factory
                .CreateSwapChainForComposition(&device, &desc, None)?
                .cast()?
        };
        // SAFETY: a waitable swap chain; the returned handle is ours to close (see Drop).
        let latency = unsafe { swap_chain.GetFrameLatencyWaitableObject() };

        let per_instance =
            |name: &'static std::ffi::CStr, format, offset| D3D11_INPUT_ELEMENT_DESC {
                SemanticName: PCSTR(name.as_ptr().cast()),
                SemanticIndex: 0,
                Format: format,
                InputSlot: 0,
                AlignedByteOffset: offset,
                InputSlotClass: D3D11_INPUT_PER_INSTANCE_DATA,
                InstanceDataStepRate: 1,
            };
        let elements = [
            per_instance(c"RECT", DXGI_FORMAT_R32G32B32A32_FLOAT, 0),
            per_instance(c"ATLAS", DXGI_FORMAT_R32G32_FLOAT, 16),
            per_instance(c"COLOR", DXGI_FORMAT_R32G32B32A32_FLOAT, 24),
        ];
        let mut blend_desc = D3D11_BLEND_DESC::default();
        // Premultiplied "over": the images already carry colour times alpha.
        blend_desc.RenderTarget[0] = D3D11_RENDER_TARGET_BLEND_DESC {
            BlendEnable: true.into(),
            SrcBlend: D3D11_BLEND_ONE,
            DestBlend: D3D11_BLEND_INV_SRC_ALPHA,
            BlendOp: D3D11_BLEND_OP_ADD,
            SrcBlendAlpha: D3D11_BLEND_ONE,
            DestBlendAlpha: D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha: D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask: D3D11_COLOR_WRITE_ENABLE_ALL.0 as u8,
        };
        let rasterizer_desc = D3D11_RASTERIZER_DESC {
            FillMode: D3D11_FILL_SOLID,
            CullMode: D3D11_CULL_NONE,
            DepthClipEnable: true.into(),
            ..Default::default()
        };
        let mut vertex_shader = None;
        let mut pixel_shader = None;
        let mut input_layout = None;
        let mut blend = None;
        let mut rasterizer = None;
        let mut constants = None;
        // SAFETY: descriptors, bytecode and output slots stay live for these synchronous calls.
        unsafe {
            device.CreateVertexShader(VERTEX_SHADER, None, Some(&mut vertex_shader))?;
            device.CreatePixelShader(PIXEL_SHADER, None, Some(&mut pixel_shader))?;
            device.CreateInputLayout(&elements, VERTEX_SHADER, Some(&mut input_layout))?;
            device.CreateBlendState(&blend_desc, Some(&mut blend))?;
            device.CreateRasterizerState(&rasterizer_desc, Some(&mut rasterizer))?;
            device.CreateBuffer(
                &D3D11_BUFFER_DESC {
                    ByteWidth: size_of::<[f32; 4]>() as u32,
                    Usage: D3D11_USAGE_DEFAULT,
                    BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
                    ..Default::default()
                },
                None,
                Some(&mut constants),
            )?;
        }
        let gpu = Gpu {
            atlas: Atlas::new(&device, ATLAS_START)?,
            device,
            context,
            swap_chain,
            latency,
            target: None,
            vertex_shader: vertex_shader.ok_or_else(fail)?,
            pixel_shader: pixel_shader.ok_or_else(fail)?,
            input_layout: input_layout.ok_or_else(fail)?,
            blend: blend.ok_or_else(fail)?,
            rasterizer: rasterizer.ok_or_else(fail)?,
            constants: constants.ok_or_else(fail)?,
            instance_buffer: None,
            instance_capacity: 0,
            width,
            height,
            mapped: None,
            shelves: Shelves::new(0, 0),
            pending: Vec::new(),
            sizes: Vec::new(),
            batches: 0,
        };
        gpu.update_constants();
        Ok(gpu)
    }

    fn update_constants(&self) {
        let scale = [2.0 / self.width as f32, 2.0 / self.height as f32, 0.0, 0.0];
        // SAFETY: the buffer holds exactly these 16 bytes; the copy is synchronous.
        unsafe {
            self.context
                .UpdateSubresource(&self.constants, 0, None, scale.as_ptr().cast(), 0, 0);
        }
    }

    /// Blocks until the swap chain wants the next frame (about one vsync after a present).
    fn wait_for_latency(&self) {
        if !self.latency.is_invalid() {
            // SAFETY: an owned, live handle. A timeout only costs pacing, so the result is unused.
            let _ = unsafe { WaitForSingleObject(self.latency, LATENCY_TIMEOUT_MS) };
        }
    }

    /// Resizes the buffers and sets the inverse composition scale, so one buffer pixel is one
    /// physical pixel whatever scale XAML applies to the panel.
    fn set_layout(&mut self, layout: &Layout) -> Result<()> {
        // SAFETY: live interfaces on their owning thread. Every reference to the back buffer is
        // released (ours, and the context's via ClearState + Flush) before ResizeBuffers.
        unsafe {
            if (layout.width, layout.height) != (self.width, self.height) {
                self.target = None;
                self.context.ClearState();
                self.context.Flush();
                self.swap_chain.ResizeBuffers(
                    BUFFER_COUNT,
                    layout.width,
                    layout.height,
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
                )?;
                self.width = layout.width;
                self.height = layout.height;
                self.update_constants();
            }
            self.swap_chain.SetMatrixTransform(&DXGI_MATRIX_3X2_F {
                _11: 1.0 / layout.scale_x,
                _22: 1.0 / layout.scale_y,
                ..Default::default()
            })
        }
    }

    /// Binds the pipeline and clears the back buffer to transparent.
    fn begin_frame(&mut self) -> Result<()> {
        // SAFETY: live interfaces on their owning thread; slices and descriptors outlive the calls.
        unsafe {
            let target = match &self.target {
                Some(target) => target.clone(),
                None => {
                    // With a flip swap chain buffer 0 is always the current back buffer.
                    let buffer: ID3D11Texture2D = self.swap_chain.GetBuffer(0)?;
                    let mut target = None;
                    self.device
                        .CreateRenderTargetView(&buffer, None, Some(&mut target))?;
                    self.target.insert(target.ok_or_else(fail)?).clone()
                }
            };
            let context = &self.context;
            context.ClearRenderTargetView(&target, &[0.0; 4]);
            // A present unbinds the target, so it is bound again for every frame.
            context.OMSetRenderTargets(Some(&[Some(target)]), None);
            context.OMSetBlendState(&self.blend, None, u32::MAX);
            context.RSSetState(&self.rasterizer);
            context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                TopLeftX: 0.0,
                TopLeftY: 0.0,
                Width: self.width as f32,
                Height: self.height as f32,
                MinDepth: 0.0,
                MaxDepth: 1.0,
            }]));
            context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            context.IASetInputLayout(&self.input_layout);
            context.VSSetShader(&self.vertex_shader, None);
            context.VSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
            context.PSSetShader(&self.pixel_shader, None);
        }
        self.sizes.clear();
        self.batches = 0;
        Ok(())
    }

    /// Copies one image's coverage into the atlas and queues its quad, drawing the open batch
    /// first when the atlas is full.
    fn push(&mut self, image: &Image) -> Result<()> {
        let (w, h) = (image.w as u32, image.h as u32);
        self.sizes.push((w, h));
        if w > self.atlas.width || h > self.atlas.height {
            let size = atlas_size_for(&[(w, h)], (self.atlas.width, self.atlas.height));
            if w > size.0 || h > size.1 {
                // Larger than any atlas (a frame beyond 4096 px): leave it out.
                return Ok(());
            }
            self.flush()?;
            self.atlas = Atlas::new(&self.device, size)?;
        }
        let mut at = self.open_batch()?.place(w, h);
        if at.is_none() {
            self.flush()?;
            at = self.open_batch()?.place(w, h);
        }
        let ((x, y), (data, pitch)) = at.zip(self.mapped).ok_or_else(fail)?;
        for row in 0..image.h {
            let source = image.row(row);
            // SAFETY: the shelf packer keeps `(x, y)..(x + w, y + h)` inside the mapped atlas, whose
            // rows are `pitch >= width` bytes apart.
            unsafe {
                let target = data.add((y as usize + row as usize) * pitch + x as usize);
                std::ptr::copy_nonoverlapping(source.as_ptr(), target, source.len());
            }
        }
        self.pending.push(Instance {
            rect: [image.dst_x as f32, image.dst_y as f32, w as f32, h as f32],
            atlas: [x as f32, y as f32],
            color: premultiplied(image.color),
        });
        Ok(())
    }

    /// Maps the atlas for a new batch unless one is open.
    fn open_batch(&mut self) -> Result<&mut Shelves> {
        if self.mapped.is_none() {
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            // SAFETY: a dynamic texture on its owning thread; DISCARD because every batch rewrites
            // all the texels it reads.
            unsafe {
                self.context.Map(
                    &self.atlas.texture,
                    0,
                    D3D11_MAP_WRITE_DISCARD,
                    0,
                    Some(&mut mapped),
                )?;
            }
            if mapped.pData.is_null() || (mapped.RowPitch as usize) < self.atlas.width as usize {
                // SAFETY: mapped just above.
                unsafe { self.context.Unmap(&self.atlas.texture, 0) };
                return Err(fail());
            }
            self.mapped = Some((mapped.pData.cast(), mapped.RowPitch as usize));
            self.shelves = Shelves::new(self.atlas.width, self.atlas.height);
        }
        Ok(&mut self.shelves)
    }

    /// Draws the open batch: unmaps the atlas, uploads the instances, one instanced draw.
    fn flush(&mut self) -> Result<()> {
        if self.mapped.take().is_some() {
            // SAFETY: mapped by `open_batch`.
            unsafe { self.context.Unmap(&self.atlas.texture, 0) };
        }
        if self.pending.is_empty() {
            return Ok(());
        }
        if self.instance_buffer.is_none() || self.instance_capacity < self.pending.len() {
            let capacity = self.pending.len().next_power_of_two().max(256);
            let bytes = u32::try_from(capacity * size_of::<Instance>()).map_err(|_| fail())?;
            let mut buffer = None;
            // SAFETY: valid descriptor and live output slot; no initial data.
            unsafe {
                self.device.CreateBuffer(
                    &D3D11_BUFFER_DESC {
                        ByteWidth: bytes,
                        Usage: D3D11_USAGE_DYNAMIC,
                        BindFlags: D3D11_BIND_VERTEX_BUFFER.0 as u32,
                        CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
                        ..Default::default()
                    },
                    None,
                    Some(&mut buffer),
                )?;
            }
            self.instance_buffer = Some(buffer.ok_or_else(fail)?);
            self.instance_capacity = capacity;
        }
        let buffer = self.instance_buffer.clone().ok_or_else(fail)?;
        let count = self.pending.len();
        // SAFETY: live interfaces on their owning thread. The mapped buffer holds at least `count`
        // instances; the binding arrays are locals that outlive the calls.
        unsafe {
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            self.context
                .Map(&buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, Some(&mut mapped))?;
            if !mapped.pData.is_null() {
                std::ptr::copy_nonoverlapping(self.pending.as_ptr(), mapped.pData.cast(), count);
            }
            self.context.Unmap(&buffer, 0);
            if mapped.pData.is_null() {
                return Err(fail());
            }
            let stride = size_of::<Instance>() as u32;
            let offset = 0u32;
            let buffer = Some(buffer);
            self.context
                .IASetVertexBuffers(0, 1, Some(&buffer), Some(&stride), Some(&offset));
            self.context
                .PSSetShaderResources(0, Some(&[Some(self.atlas.view.clone())]));
            self.context.DrawInstanced(4, count as u32, 0, 0);
        }
        self.pending.clear();
        self.batches += 1;
        Ok(())
    }

    /// Draws what is queued; when the frame needed several batches the atlas grows for the next one.
    fn end_frame(&mut self) -> Result<()> {
        self.flush()?;
        if self.batches > 1 {
            let current = (self.atlas.width, self.atlas.height);
            let size = atlas_size_for(&self.sizes, current);
            if size != current {
                self.atlas = Atlas::new(&self.device, size)?;
            }
        }
        Ok(())
    }

    /// Renders `track` at `now_ms` and draws its images into the back buffer. `None` when libass
    /// reports no change and `force` is off: nothing was drawn, there is nothing to present.
    fn draw_track(
        &mut self,
        session: &Session,
        track: &Track<'_>,
        now_ms: i64,
        force: bool,
    ) -> Result<Option<Drawn>> {
        let start = Instant::now();
        let mut begun = false;
        let mut error = None;
        let rendered = session.render(track, now_ms, force, |image| {
            if error.is_some() {
                return;
            }
            let mut step = || {
                if !std::mem::replace(&mut begun, true) {
                    self.begin_frame()?;
                }
                self.push(image)
            };
            error = step().err();
        });
        if let Some(error) = error {
            let _ = self.flush();
            return Err(error);
        }
        if rendered.changed == 0 && !force {
            return Ok(None);
        }
        if !begun {
            // The change is that nothing is shown any more.
            self.begin_frame()?;
        }
        self.end_frame()?;
        Ok(Some(Drawn {
            images: rendered.images,
            elapsed: start.elapsed(),
        }))
    }

    /// A fully transparent frame.
    fn draw_empty(&mut self) -> Result<()> {
        self.begin_frame()
    }

    fn present(&mut self) -> Result<()> {
        // SAFETY: live swap chain on its owning thread. Sync interval 1: one frame per vsync.
        unsafe { self.swap_chain.Present(1, DXGI_PRESENT(0)) }.ok()
    }

    /// Copies the drawn (not yet presented) back buffer out.
    fn read_back(&mut self) -> Result<Capture> {
        let (width, height) = (self.width, self.height);
        let mut bgra = vec![0u8; width as usize * height as usize * 4];
        // SAFETY: live interfaces on their owning thread; the staging texture matches the back
        // buffer, and each mapped row has at least `width * 4` readable bytes.
        unsafe {
            let buffer: ID3D11Texture2D = self.swap_chain.GetBuffer(0)?;
            let mut desc = D3D11_TEXTURE2D_DESC::default();
            buffer.GetDesc(&mut desc);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
            desc.MiscFlags = 0;
            let mut staging = None;
            self.device
                .CreateTexture2D(&desc, None, Some(&mut staging))?;
            let staging: ID3D11Texture2D = staging.ok_or_else(fail)?;
            self.context.CopyResource(&staging, &buffer);
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            self.context
                .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
            let row_bytes = width as usize * 4;
            let readable = !mapped.pData.is_null() && mapped.RowPitch as usize >= row_bytes;
            if readable {
                for (y, row) in bgra.chunks_exact_mut(row_bytes).enumerate() {
                    let source = mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize);
                    std::ptr::copy_nonoverlapping(source, row.as_mut_ptr(), row_bytes);
                }
            }
            self.context.Unmap(&staging, 0);
            if !readable {
                return Err(fail());
            }
        }
        Ok(Capture {
            width,
            height,
            bgra,
        })
    }
}
