//! Windows side of the exclusive-mode output (docs/audio-passthrough.md, "Exclusive output"): the
//! `bale_audio_out_*` exports, the live-output table and the render thread. The render thread makes every COM
//! call (activation, `Initialize`, `Start`/`Stop`/`Reset`, the buffer and the clock); the exports only change
//! shared state under a short lock and wake it, so none of them waits on the audio stack except create (for the
//! activation) and destroy (for the thread to stop).

use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::mpsc::{Receiver, RecvTimeoutError, SyncSender, sync_channel};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, OnceLock, PoisonError};
use std::thread::JoinHandle;
use std::time::Duration;

use windows::Media::Devices::{AudioDeviceRole, MediaDevice};
use windows::Win32::Foundation::{
    CloseHandle, FreeLibrary, HANDLE, HMODULE, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows::Win32::Media::Audio::{
    AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_E_BUFFER_TOO_LARGE, AUDCLNT_SHAREMODE_EXCLUSIVE,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK, AUDCLNT_STREAMOPTIONS_NONE, AudioCategory_Movie,
    AudioClientProperties, IAudioClient, IAudioClient2, IAudioClock, IAudioRenderClient,
    WAVEFORMATEX,
};
use windows::Win32::System::LibraryLoader::{
    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GetModuleHandleExW, GetProcAddress,
};
use windows::Win32::System::Performance::QueryPerformanceFrequency;
use windows::Win32::System::Threading::{
    CreateEventW, INFINITE, SetEvent, WaitForMultipleObjects, WaitForSingleObject,
};
use windows::core::{BOOL, HRESULT, Interface, PCWSTR, s, w};

use super::render::{evaluate, fill_period};
use super::ring::Ring;
use super::sync::{
    Reference, Sample, SyncConfig, SyncController, position_frames, position_hns, qpc_hns,
};
use super::table::IdTable;
use super::timeline::Timeline;
use super::{
    AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED, AUDCLNT_E_UNSUPPORTED_FORMAT, BaleAudioFormat,
    BaleAudioOutOpenInfo, BaleAudioOutStats, E_HANDLE, E_INVALIDARG, E_NOT_SET, E_OUTOFMEMORY,
    E_POINTER, E_TIMEOUT, E_UNEXPECTED, Format, MMCSS_FAILED, MMCSS_NONE, MMCSS_PRO_AUDIO,
    PTS_NONE, S_OK, STATUS_DEVICE_LOST, STATUS_FAILED, STATUS_PAUSED, STATUS_RUNNING,
    STATUS_STOPPED, STEP_ACTIVATE, STEP_ARGUMENTS, STEP_DEVICE_ID, STEP_FORMAT, STEP_INITIALIZE,
    STEP_MODULE, STEP_SERVICES, STEP_TABLE, STEP_THREAD, WRITE_CLOSED, aligned_period_hns,
    is_device_lost, ring_frames, valid_ring_ms,
};
use crate::audio_probe::wasapi::{
    ActivateAudioInterfaceAsync, Failed, Mta, activate_client, activation_function,
};

/// Live outputs at once. An endpoint takes one exclusive stream; the rest is headroom for an output being
/// destroyed while the next is created.
const MAX_OUTPUTS: usize = 8;
/// How long create waits for the render thread to open the device: two activations (2 s each; the second
/// only after `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`) and the format calls.
const OPEN_TIMEOUT: Duration = Duration::from_secs(6);
/// How long destroy waits for the render thread to stop the stream and release the client.
const DESTROY_TIMEOUT: Duration = Duration::from_secs(3);
/// A started stream without a buffer event for this long has stalled and fails with
/// `HRESULT_FROM_WIN32(ERROR_TIMEOUT)`, as in Microsoft's exclusive-mode sample (2 s).
const EVENT_TIMEOUT_MS: u32 = 2_000;
/// Timeline kept behind the device position (a reference is a little older than the position read after it).
const TIMELINE_KEEP_MS: u64 = 500;
/// The render thread's stack reservation: Windows' default for a new thread (committed only as used), since
/// the audio stack's own depth inside `Initialize` and the activation is not ours to bound.
const STACK_SIZE: usize = 1024 * 1024;

static OUTPUTS: Mutex<IdTable<Output>> = Mutex::new(IdTable::new(MAX_OUTPUTS));

fn outputs() -> MutexGuard<'static, IdTable<Output>> {
    OUTPUTS.lock().unwrap_or_else(PoisonError::into_inner)
}

fn output(id: u32) -> Option<Arc<Output>> {
    outputs().get(id)
}

/// QueryPerformanceFrequency, read once (fixed at boot).
fn qpc_frequency() -> i64 {
    static FREQUENCY: OnceLock<i64> = OnceLock::new();
    *FREQUENCY.get_or_init(|| {
        let mut frequency = 0i64;
        // SAFETY: a valid out-pointer; the call cannot fail on Windows XP and later.
        let _ = unsafe { QueryPerformanceFrequency(&mut frequency) };
        frequency
    })
}

/// An auto-reset event, closed on drop.
struct Event(HANDLE);

// SAFETY: an event handle is usable from any thread; this wrapper only closes it once, on drop.
unsafe impl Send for Event {}
// SAFETY: as above; shared access only sets or waits on it.
unsafe impl Sync for Event {}

impl Event {
    fn new() -> windows::core::Result<Self> {
        // SAFETY: an unnamed auto-reset event with default security, initially not set.
        Ok(Self(unsafe {
            CreateEventW(None, false, false, PCWSTR::null())?
        }))
    }

    fn set(&self) {
        // SAFETY: the handle stays open while `self` lives.
        let _ = unsafe { SetEvent(self.0) };
    }
}

impl Drop for Event {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            // SAFETY: this wrapper owns the handle and closes it once.
            let _ = unsafe { CloseHandle(self.0) };
        }
    }
}

/// A reference on a loaded module, released on drop: the caller's `mmdevapi.dll` stays loaded while the
/// output's client lives, whatever the caller does after create.
struct ModulePin(HMODULE);

// SAFETY: a module handle is a process-wide value; this wrapper only frees its own reference, once.
unsafe impl Send for ModulePin {}

impl ModulePin {
    /// Adds a reference to the module holding `address`.
    fn holding(address: *const c_void) -> Result<Self, i32> {
        let mut module = HMODULE::default();
        // SAFETY: FROM_ADDRESS takes `address` as an address inside a loaded module, not as a name; the
        // out-slot is a valid local.
        unsafe {
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                PCWSTR(address.cast()),
                &mut module,
            )
        }
        .map_err(|error| error.code().0)?;
        Ok(Self(module))
    }
}

impl Drop for ModulePin {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            // SAFETY: releases the one reference `holding` added.
            let _ = unsafe { FreeLibrary(self.0) };
        }
    }
}

/// avrt.dll's `AvSetMmThreadCharacteristicsW` and `AvRevertMmThreadCharacteristics`.
type AvSetMmThreadCharacteristicsW = unsafe extern "system" fn(PCWSTR, *mut u32) -> HANDLE;
type AvRevertMmThreadCharacteristics = unsafe extern "system" fn(HANDLE) -> BOOL;

/// The render thread in MMCSS "Pro Audio" (Microsoft's exclusive-mode sample), when avrt.dll is already loaded
/// in the process (by the app or Media Foundation): bale_gpu never loads or imports it, so a console without
/// MMCSS for apps keeps normal priority.
struct Mmcss {
    module: HMODULE,
    revert: Option<AvRevertMmThreadCharacteristics>,
    task: HANDLE,
    status: u32,
}

impl Mmcss {
    fn enter() -> Self {
        let mut module = HMODULE::default();
        // SAFETY: a name lookup that adds a reference only if avrt.dll is already loaded; never loads it.
        if unsafe { GetModuleHandleExW(0, w!("avrt.dll"), &mut module) }.is_err() {
            return Self::none(HMODULE::default(), MMCSS_NONE);
        }
        // SAFETY: the module is loaded and referenced; the names are static C strings.
        let (set, revert) = unsafe {
            (
                GetProcAddress(module, s!("AvSetMmThreadCharacteristicsW")),
                GetProcAddress(module, s!("AvRevertMmThreadCharacteristics")),
            )
        };
        let (Some(set), Some(revert)) = (set, revert) else {
            return Self::none(module, MMCSS_NONE);
        };
        // SAFETY: avrt.h's documented signatures; both are plain `system` function pointers.
        let (set, revert) = unsafe {
            (
                std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    AvSetMmThreadCharacteristicsW,
                >(set),
                std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    AvRevertMmThreadCharacteristics,
                >(revert),
            )
        };
        let mut index = 0u32;
        // SAFETY: a static task name and a valid out-slot; applies to the calling (render) thread.
        let task = unsafe { set(w!("Pro Audio"), &mut index) };
        if task.is_invalid() {
            return Self::none(module, MMCSS_FAILED);
        }
        Self {
            module,
            revert: Some(revert),
            task,
            status: MMCSS_PRO_AUDIO,
        }
    }

    fn none(module: HMODULE, status: u32) -> Self {
        Self {
            module,
            revert: None,
            task: HANDLE::default(),
            status,
        }
    }
}

impl Drop for Mmcss {
    fn drop(&mut self) {
        if let Some(revert) = self.revert {
            // SAFETY: the task handle from AvSetMmThreadCharacteristicsW on this same thread.
            let _ = unsafe { revert(self.task) };
        }
        if !self.module.is_invalid() {
            // SAFETY: releases the reference GetModuleHandleExW added.
            let _ = unsafe { FreeLibrary(self.module) };
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Desired {
    Stopped,
    Running,
    Paused,
}

/// What the exports and the render thread share, under `Shared::state`.
struct State {
    ring: Ring,
    desired: Desired,
    /// Flush requests (also the flush generation the render thread compares).
    flushes: u32,
    destroy: bool,
    /// The newest sync reference, taken by the render thread.
    reference: Option<Reference>,
    /// The error that ended the stream.
    dead: Option<i32>,
    /// Kept up to date by the render thread (and `frames_written` / `flushes` by the exports).
    stats: BaleAudioOutStats,
}

struct Shared {
    format: Format,
    wake: Event,
    state: Mutex<State>,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// A live output: its shared state and its render thread (with the channel that closes when it ends).
struct Output {
    shared: Arc<Shared>,
    thread: Mutex<Option<(JoinHandle<()>, Receiver<()>)>>,
}

impl Output {
    /// Stops the render thread and waits for it to release everything (at most [`DESTROY_TIMEOUT`]; a
    /// thread stuck in the audio stack is left to finish on its own).
    fn shut_down(&self) -> i32 {
        self.shared.lock().destroy = true;
        self.shared.wake.set();
        let thread = self
            .thread
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        let Some((thread, exited)) = thread else {
            return S_OK;
        };
        match exited.recv_timeout(DESTROY_TIMEOUT) {
            Err(RecvTimeoutError::Timeout) => E_TIMEOUT,
            _ => {
                let _ = thread.join();
                S_OK
            }
        }
    }
}

type Opened = Result<(Arc<Shared>, BaleAudioOutOpenInfo), BaleAudioOutOpenInfo>;

enum Opening {
    Waiting,
    Done(Opened),
    Taken,
    Abandoned,
}

/// Create waits here for the render thread's open. Under one lock, so a thread finishing just as create gives
/// up sees that and releases the device instead of running unowned.
struct Handoff {
    slot: Mutex<Opening>,
    ready: Condvar,
}

impl Handoff {
    fn new() -> Self {
        Self {
            slot: Mutex::new(Opening::Waiting),
            ready: Condvar::new(),
        }
    }

    /// Hands over the open's result; false when create stopped waiting (the thread must clean up).
    fn deliver(&self, opened: Opened) -> bool {
        let mut slot = self.slot.lock().unwrap_or_else(PoisonError::into_inner);
        if matches!(*slot, Opening::Abandoned) {
            return false;
        }
        *slot = Opening::Done(opened);
        self.ready.notify_all();
        true
    }

    fn wait(&self, timeout: Duration) -> Option<Opened> {
        let slot = self.slot.lock().unwrap_or_else(PoisonError::into_inner);
        let (mut slot, _) = self
            .ready
            .wait_timeout_while(slot, timeout, |slot| matches!(slot, Opening::Waiting))
            .unwrap_or_else(PoisonError::into_inner);
        match std::mem::replace(&mut *slot, Opening::Taken) {
            Opening::Done(opened) => Some(opened),
            _ => {
                *slot = Opening::Abandoned;
                None
            }
        }
    }
}

fn guard(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED)
}

/// Creates an exclusive-mode output on the default render endpoint (`include/bale_audio_out.h`). Blocks for
/// the activation and the format calls (normally milliseconds, at most about 6 s); never call it on the UI
/// thread. Writes the id (1-65535) to `id` on success, else 0; `info` (optional) gets what was done either way.
///
/// # Safety
/// `mmdevapi` must be null or a module the caller keeps loaded for the call; `format` must point to a
/// `BaleAudioFormat`; `id` to a writable `u32`; `info` must be null or point to a writable
/// `BaleAudioOutOpenInfo`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_audio_out_create(
    mmdevapi: HMODULE,
    format: *const BaleAudioFormat,
    ring_ms: u32,
    id: *mut u32,
    info: *mut BaleAudioOutOpenInfo,
) -> i32 {
    let mut report = BaleAudioOutOpenInfo::default();
    let hr = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: the caller's pointers, as documented above.
        unsafe { create(mmdevapi, format, ring_ms, id, &mut report) }
    }))
    .unwrap_or_else(|_| {
        report.step = STEP_THREAD;
        E_UNEXPECTED
    });
    report.hr = hr;
    if !info.is_null() {
        // SAFETY: the caller guarantees a writable struct; unaligned writes accept any address.
        unsafe { info.write_unaligned(report) };
    }
    hr
}

/// # Safety
/// As [`bale_audio_out_create`].
unsafe fn create(
    mmdevapi: HMODULE,
    format: *const BaleAudioFormat,
    ring_ms: u32,
    id: *mut u32,
    info: &mut BaleAudioOutOpenInfo,
) -> i32 {
    let fail = |info: &mut BaleAudioOutOpenInfo, step, hr| {
        info.step = step;
        hr
    };
    if id.is_null() || format.is_null() {
        return fail(info, STEP_ARGUMENTS, E_POINTER);
    }
    // SAFETY: the caller guarantees a writable `u32` and a readable format.
    let requested = unsafe {
        id.write_unaligned(0);
        format.read_unaligned()
    };
    let format = match Format::from_abi(&requested) {
        Ok(format) => format,
        Err(hr) => return fail(info, STEP_ARGUMENTS, hr),
    };
    if !valid_ring_ms(ring_ms) {
        return fail(info, STEP_ARGUMENTS, E_INVALIDARG);
    }
    let activate = match activation_function(mmdevapi) {
        Ok(activate) => activate,
        Err(Failed(_, hr)) => return fail(info, STEP_MODULE, hr),
    };
    let pin = match ModulePin::holding(activate as *const c_void) {
        Ok(pin) => pin,
        Err(hr) => return fail(info, STEP_MODULE, hr),
    };
    if outputs().is_full() {
        return fail(info, STEP_TABLE, E_OUTOFMEMORY);
    }
    let handoff = Arc::new(Handoff::new());
    let (exit, exited) = sync_channel::<()>(0);
    let thread = std::thread::Builder::new()
        .name("bale-audio-out".into())
        .stack_size(STACK_SIZE)
        .spawn({
            let handoff = Arc::clone(&handoff);
            move || render_thread(activate, pin, format, ring_ms, &handoff, exit)
        });
    let Ok(thread) = thread else {
        return fail(info, STEP_THREAD, E_OUTOFMEMORY);
    };
    match handoff.wait(OPEN_TIMEOUT) {
        // The thread sees the abandoned handoff, releases the device and ends on its own.
        None => fail(info, STEP_THREAD, E_TIMEOUT),
        Some(Err(failed)) => {
            *info = failed;
            let _ = thread.join();
            failed.hr
        }
        Some(Ok((shared, opened))) => {
            *info = opened;
            let output = Arc::new(Output {
                shared,
                thread: Mutex::new(Some((thread, exited))),
            });
            let inserted = outputs().insert(Arc::clone(&output));
            match inserted {
                Some(new_id) => {
                    // SAFETY: checked non-null above; the caller guarantees it is writable.
                    unsafe { id.write_unaligned(new_id) };
                    S_OK
                }
                None => {
                    output.shut_down();
                    fail(info, STEP_TABLE, E_OUTOFMEMORY)
                }
            }
        }
    }
}

/// Queues interleaved frames for the device and returns the bytes taken, a multiple of the format's block
/// size: 0 when the ring is full (retry after a few milliseconds), [`WRITE_CLOSED`] for an unknown id or an
/// output that can no longer play. Never waits for the device.
///
/// # Safety
/// `data` must point to `bytes` readable bytes (or be null with `bytes` 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_audio_out_write(
    id: u32,
    data: *const c_void,
    bytes: u32,
    pts_100ns: i64,
) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(output) = output(id) else {
            return WRITE_CLOSED;
        };
        let mut state = output.shared.lock();
        if state.destroy || state.dead.is_some() {
            return WRITE_CLOSED;
        }
        if data.is_null() || bytes == 0 {
            return 0;
        }
        // SAFETY: the caller guarantees `bytes` readable bytes at `data`.
        let data = unsafe { std::slice::from_raw_parts(data.cast::<u8>(), bytes as usize) };
        let pts = (pts_100ns != PTS_NONE).then_some(pts_100ns);
        let taken = state.ring.write(data, pts);
        state.stats.frames_written += (taken / output.shared.format.block_align()) as u64;
        taken as u32
    }))
    .unwrap_or(WRITE_CLOSED)
}

/// Runs `change` on a live output's state and wakes its render thread.
fn control(id: u32, wake: bool, change: impl FnOnce(&mut State)) -> i32 {
    guard(|| {
        let Some(output) = output(id) else {
            return E_HANDLE;
        };
        {
            let mut state = output.shared.lock();
            if state.destroy {
                return E_HANDLE;
            }
            if let Some(hr) = state.dead {
                return hr;
            }
            change(&mut state);
        }
        if wake {
            output.shared.wake.set();
        }
        S_OK
    })
}

/// Starts (or resumes) playback of the queued frames. Idempotent.
#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_start(id: u32) -> i32 {
    control(id, true, |state| state.desired = Desired::Running)
}

/// Pauses playback; queued frames and the device buffer stay. Idempotent.
#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_pause(id: u32) -> i32 {
    control(id, true, |state| state.desired = Desired::Paused)
}

/// Drops everything queued and resets the device stream (a seek or stream switch): frames written after the
/// call start a new timeline at their pts, and play on if the output was started.
#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_flush(id: u32) -> i32 {
    control(id, true, |state| {
        state.ring.clear();
        state.reference = None;
        state.flushes = state.flushes.wrapping_add(1);
        state.stats.flushes = state.flushes;
    })
}

/// A sync reference: the video position (100 ns) at a QueryPerformanceCounter value. The render thread
/// measures its error at its next wake and corrects beyond 20 ms.
#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_sync(id: u32, video_position_100ns: i64, qpc_ticks: i64) -> i32 {
    if qpc_ticks <= 0 {
        return if output(id).is_some() {
            E_INVALIDARG
        } else {
            E_HANDLE
        };
    }
    let reference = Reference {
        video_hns: video_position_100ns,
        qpc_hns: qpc_hns(qpc_ticks, qpc_frequency()),
    };
    control(id, true, |state| state.reference = Some(reference))
}

/// Copies the output's counters into `stats` without waiting for the render thread.
///
/// # Safety
/// `stats` must point to a writable `BaleAudioOutStats`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_audio_out_stats(id: u32, stats: *mut BaleAudioOutStats) -> i32 {
    if stats.is_null() {
        return E_POINTER;
    }
    guard(|| {
        let Some(output) = output(id) else {
            return E_HANDLE;
        };
        let rate = u64::from(output.shared.format.rate());
        let snapshot = {
            let state = output.shared.lock();
            let mut snapshot = state.stats;
            let queued = state.ring.len() as u64;
            snapshot.queued_frames = queued as u32;
            snapshot.queued_ms = (queued * 1000 / rate) as u32;
            snapshot
        };
        // SAFETY: the caller guarantees a writable struct; unaligned writes accept any address.
        unsafe { stats.write_unaligned(snapshot) };
        S_OK
    })
}

/// The format the output was created with, defaults filled in (so a feeder handed only the id knows what to
/// convert to).
///
/// # Safety
/// `format` must point to a writable `BaleAudioFormat`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_audio_out_format(id: u32, format: *mut BaleAudioFormat) -> i32 {
    if format.is_null() {
        return E_POINTER;
    }
    guard(|| {
        let Some(output) = output(id) else {
            return E_HANDLE;
        };
        // SAFETY: the caller guarantees a writable struct; unaligned writes accept any address.
        unsafe { format.write_unaligned(output.shared.format.abi) };
        S_OK
    })
}

/// Stops the stream, ends the render thread and releases the client; the id names nothing afterwards. Safe
/// in any state, also after the device was lost. Waits for the thread (at most 3 s; `E_TIMEOUT` leaves a stuck
/// thread to finish on its own).
#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_destroy(id: u32) -> i32 {
    guard(|| {
        let removed = outputs().remove(id);
        match removed {
            Some(output) => output.shut_down(),
            None => E_HANDLE,
        }
    })
}

/// The render thread: opens the device, hands the result to create, then renders until destroyed. The module
/// pin and the exit channel go last, after every COM object is released.
fn render_thread(
    activate: ActivateAudioInterfaceAsync,
    pin: ModulePin,
    format: Format,
    ring_ms: u32,
    handoff: &Handoff,
    exit: SyncSender<()>,
) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        run_thread(activate, &format, ring_ms, handoff)
    }));
    drop(pin);
    drop(exit);
}

fn run_thread(
    activate: ActivateAudioInterfaceAsync,
    format: &Format,
    ring_ms: u32,
    handoff: &Handoff,
) {
    let mut info = BaleAudioOutOpenInfo::default();
    // Dropped last: every COM object below is released before the thread leaves the apartment.
    let _mta = match Mta::join() {
        Ok(mta) => mta,
        Err(Failed(_, hr)) => {
            info.step = STEP_THREAD;
            info.hr = hr;
            handoff.deliver(Err(info));
            return;
        }
    };
    let mmcss = Mmcss::enter();
    info.mmcss = mmcss.status;
    let device = match Device::open(activate, format, &mut info) {
        Ok(device) => device,
        Err(hr) => {
            info.hr = hr;
            handoff.deliver(Err(info));
            return;
        }
    };
    let wake = match Event::new() {
        Ok(wake) => wake,
        Err(error) => {
            info.step = STEP_THREAD;
            info.hr = error.code().0;
            handoff.deliver(Err(info));
            return;
        }
    };
    info.ring_frames = ring_frames(ring_ms, format.rate(), device.buffer_frames);
    let shared = Arc::new(Shared {
        format: *format,
        wake,
        state: Mutex::new(State {
            ring: Ring::new(
                info.ring_frames as usize,
                format.block_align(),
                format.rate(),
            ),
            desired: Desired::Stopped,
            flushes: 0,
            destroy: false,
            reference: None,
            dead: None,
            stats: BaleAudioOutStats {
                buffer_frames: info.buffer_frames,
                ring_frames: info.ring_frames,
                device_pts_100ns: PTS_NONE,
                ..BaleAudioOutStats::default()
            },
        }),
    });
    if !handoff.deliver(Ok((Arc::clone(&shared), info))) {
        return;
    }
    // A panic in the loop fails the output like a device error: the caller sees FAILED and destroys it.
    let rendered =
        catch_unwind(AssertUnwindSafe(|| render(&shared, &device))).unwrap_or(Err(E_UNEXPECTED));
    if let Err(hr) = rendered {
        let _ = device.stop();
        {
            let mut state = shared.lock();
            state.dead = Some(hr);
            state.stats.status = if is_device_lost(hr) {
                STATUS_DEVICE_LOST
            } else {
                STATUS_FAILED
            };
            state.stats.error = hr;
        }
        // The counters stay readable until the caller destroys the output.
        while !shared.lock().destroy {
            // SAFETY: the wake event stays open while `shared` lives.
            let _ = unsafe { WaitForSingleObject(shared.wake.0, INFINITE) };
        }
    }
    let _ = device.stop();
    drop(device);
    drop(mmcss);
}

/// The initialized exclusive-mode stream. Fields drop in order: the client before its event.
struct Device {
    client: IAudioClient,
    render: IAudioRenderClient,
    clock: IAudioClock,
    event: Event,
    buffer_frames: u32,
    frequency: u64,
    rate: u32,
    block: usize,
}

fn code(error: windows::core::Error) -> i32 {
    error.code().0
}

impl Device {
    fn open(
        activate: ActivateAudioInterfaceAsync,
        format: &Format,
        info: &mut BaleAudioOutOpenInfo,
    ) -> Result<Self, i32> {
        let fail = |info: &mut BaleAudioOutOpenInfo, step, hr| {
            info.step = step;
            hr
        };
        let id = MediaDevice::GetDefaultAudioRenderId(AudioDeviceRole::Default)
            .map_err(|error| fail(info, STEP_DEVICE_ID, code(error)))?;
        if id.is_empty() {
            return Err(fail(info, STEP_DEVICE_ID, E_NOT_SET));
        }
        let endpoint = PCWSTR(id.as_ptr());
        let mut client = Self::activate(activate, endpoint, info, true)?;
        let wave = std::ptr::from_ref(&format.wave).cast::<WAVEFORMATEX>();
        // SAFETY: a live client; `wave` points to a whole WaveFormat, which covers the WAVEFORMATEX and the
        // cbSize extra bytes it declares. Exclusive mode takes no closest-match out-pointer.
        let supported =
            unsafe { client.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, wave, None) };
        info.format_hr = supported.0;
        if supported.0 != S_OK {
            // Exclusive mode answers S_OK or an error; anything else is no.
            let hr = if supported.is_err() {
                supported.0
            } else {
                AUDCLNT_E_UNSUPPORTED_FORMAT
            };
            return Err(fail(info, STEP_FORMAT, hr));
        }
        let (mut default, mut minimum) = (0i64, 0i64);
        // SAFETY: a live client; both out-slots are valid locals.
        unsafe {
            client.GetDevicePeriod(
                Some(std::ptr::from_mut(&mut default)),
                Some(std::ptr::from_mut(&mut minimum)),
            )
        }
        .map_err(|error| fail(info, STEP_INITIALIZE, code(error)))?;
        info.default_period_100ns = default;
        info.minimum_period_100ns = minimum;
        let mut period = default;
        let first = initialize(&client, period, wave);
        info.initialize_hr = first;
        if first == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED {
            // Microsoft's recipe: the aligned size from this client, then a new client with that duration.
            // SAFETY: a live client; after this failure GetBufferSize returns the next aligned size.
            let frames = unsafe { client.GetBufferSize() }
                .map_err(|error| fail(info, STEP_INITIALIZE, code(error)))?;
            drop(client);
            period = aligned_period_hns(frames, format.rate());
            client = Self::activate(activate, endpoint, info, false)?;
            info.realigned = 1;
            let second = initialize(&client, period, wave);
            info.realign_hr = second;
            if second != S_OK {
                return Err(fail(info, STEP_INITIALIZE, second));
            }
        } else if first != S_OK {
            return Err(fail(info, STEP_INITIALIZE, first));
        }
        info.period_100ns = period;
        let services =
            |info: &mut BaleAudioOutOpenInfo, error| fail(info, STEP_SERVICES, code(error));
        let event = Event::new().map_err(|error| services(info, error))?;
        // SAFETY: a live, initialized event-driven client; the event outlives it (dropped after it).
        unsafe { client.SetEventHandle(event.0) }.map_err(|error| services(info, error))?;
        // SAFETY: as above.
        let buffer_frames =
            unsafe { client.GetBufferSize() }.map_err(|error| services(info, error))?;
        // SAFETY: as above.
        let render: IAudioRenderClient =
            unsafe { client.GetService() }.map_err(|error| services(info, error))?;
        // SAFETY: as above.
        let clock: IAudioClock =
            unsafe { client.GetService() }.map_err(|error| services(info, error))?;
        // SAFETY: a live clock.
        let frequency = unsafe { clock.GetFrequency() }.map_err(|error| services(info, error))?;
        if buffer_frames == 0 || frequency == 0 {
            return Err(fail(info, STEP_SERVICES, E_UNEXPECTED));
        }
        info.buffer_frames = buffer_frames;
        Ok(Self {
            client,
            render,
            clock,
            event,
            buffer_frames,
            frequency,
            rate: format.rate(),
            block: format.block_align(),
        })
    }

    /// Activates a client on `endpoint` and sets `AudioCategory_Movie` on it, as the probe does (also on the
    /// client of a re-aligned open). The first activation's result, time and category answer go into `info`.
    fn activate(
        activate: ActivateAudioInterfaceAsync,
        endpoint: PCWSTR,
        info: &mut BaleAudioOutOpenInfo,
        first: bool,
    ) -> Result<IAudioClient, i32> {
        let (client, elapsed) = activate_client(activate, endpoint).map_err(|Failed(_, hr)| {
            info.activate_hr = hr;
            info.step = STEP_ACTIVATE;
            hr
        })?;
        let properties = match client.cast::<IAudioClient2>() {
            Ok(client2) => {
                let properties = AudioClientProperties {
                    cbSize: size_of::<AudioClientProperties>() as u32,
                    bIsOffload: BOOL(0),
                    eCategory: AudioCategory_Movie,
                    Options: AUDCLNT_STREAMOPTIONS_NONE,
                };
                // SAFETY: a live client; `properties` is a valid local for the call.
                unsafe { client2.SetClientProperties(&properties) }.map_or_else(code, |()| S_OK)
            }
            Err(error) => code(error),
        };
        if first {
            info.activate_hr = S_OK;
            info.activate_ms = elapsed.as_millis().min(u128::from(u32::MAX)) as u32;
            info.properties_hr = properties;
        }
        Ok(client)
    }

    fn start(&self) -> Result<(), i32> {
        // SAFETY: a live, initialized client.
        unsafe { self.client.Start() }.map_err(code)
    }

    /// Stops the stream (`S_FALSE` when already stopped counts as success).
    fn stop(&self) -> Result<(), i32> {
        // SAFETY: a live, initialized client.
        unsafe { self.client.Stop() }.map_err(code)
    }

    /// Flushes the stopped stream and sets its clock position back to 0.
    fn reset(&self) -> Result<(), i32> {
        // SAFETY: a live, initialized, stopped client.
        unsafe { self.client.Reset() }.map_err(code)
    }

    fn position(&self) -> Result<Sample, i32> {
        let (mut position, mut qpc) = (0u64, 0u64);
        // SAFETY: a live clock; both out-slots are valid locals.
        unsafe {
            self.clock
                .GetPosition(&mut position, Some(std::ptr::from_mut(&mut qpc)))
        }
        .map_err(code)?;
        Ok(Sample {
            frames: position_frames(position, self.frequency, self.rate),
            hns: position_hns(position, self.frequency),
            qpc_hns: qpc.min(i64::MAX as u64) as i64,
        })
    }
}

/// `Initialize` in exclusive, event-driven mode with buffer duration = periodicity = `period`.
fn initialize(client: &IAudioClient, period: i64, format: *const WAVEFORMATEX) -> i32 {
    // SAFETY: a live client; `format` points to the whole format block (see `Device::open`).
    unsafe {
        client.Initialize(
            AUDCLNT_SHAREMODE_EXCLUSIVE,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            period,
            period,
            format,
            None,
        )
    }
    .map_or_else(code, |()| S_OK)
}

/// The render loop's own state (the render thread's alone).
struct Render {
    timeline: Timeline,
    sync: SyncController,
    flushes: u32,
    started: bool,
    primed: bool,
    ever_started: bool,
    sample: Option<Sample>,
}

/// Renders until destroy (`Ok`) or a device error (`Err`, the HRESULT).
fn render(shared: &Shared, device: &Device) -> Result<(), i32> {
    let format = &shared.format;
    let rate = format.rate();
    let keep = u64::from(rate) * TIMELINE_KEEP_MS / 1000;
    let mut r = Render {
        timeline: Timeline::new(rate),
        sync: SyncController::new(SyncConfig::for_bitstream(format.is_bitstream()), rate),
        flushes: 0,
        started: false,
        primed: false,
        ever_started: false,
        sample: None,
    };
    let handles = [device.event.0, shared.wake.0];
    loop {
        let was_started = r.started;
        // SAFETY: both handles stay open for the whole loop.
        let wait = unsafe { WaitForMultipleObjects(&handles, false, EVENT_TIMEOUT_MS) };
        if wait == WAIT_FAILED {
            return Err(HRESULT::from_thread().0);
        }
        let (desired, flushes, destroy, reference) = {
            let mut state = shared.lock();
            (
                state.desired,
                state.flushes,
                state.destroy,
                state.reference.take(),
            )
        };
        if destroy {
            return Ok(());
        }
        if flushes != r.flushes {
            r.flushes = flushes;
            device.stop()?;
            device.reset()?;
            r.started = false;
            r.primed = false;
            r.sample = None;
            r.timeline.reset();
            r.sync.reset();
        }
        match desired {
            Desired::Running if !r.started => {
                if !r.primed {
                    // Load the first buffer before Start (Microsoft's exclusive-mode sample).
                    fill(shared, device, &mut r)?;
                    r.primed = true;
                }
                device.start()?;
                r.started = true;
                r.ever_started = true;
            }
            Desired::Stopped | Desired::Paused if r.started => {
                device.stop()?;
                r.started = false;
            }
            _ => {}
        }
        if r.started {
            if wait == WAIT_OBJECT_0 {
                fill(shared, device, &mut r)?;
            } else if wait == WAIT_TIMEOUT && was_started {
                return Err(E_TIMEOUT);
            }
            let sample = device.position()?;
            r.timeline.prune(sample.frames.saturating_sub(keep));
            r.sample = Some(sample);
        }
        let next_pts = reference.map(|_| shared.lock().ring.next_pts());
        if let Some(reference) = reference {
            match r.sample {
                Some(sample) if r.started => {
                    evaluate(
                        &mut r.sync,
                        &r.timeline,
                        next_pts.flatten(),
                        &sample,
                        &reference,
                        rate,
                    );
                }
                _ => {
                    r.sync.unmeasured();
                }
            }
        }
        publish(shared, &r);
    }
}

/// Fills one device period: from the ring, or silence while a flush waits to be applied.
fn fill(shared: &Shared, device: &Device, r: &mut Render) -> Result<(), i32> {
    let frames = device.buffer_frames;
    // SAFETY: a live render client of a started or primed-before-start stream.
    let data = match unsafe { device.render.GetBuffer(frames) } {
        Ok(data) => data,
        // No free buffer: an event left over from before a pause or reset.
        Err(error) if error.code() == AUDCLNT_E_BUFFER_TOO_LARGE => return Ok(()),
        Err(error) => return Err(code(error)),
    };
    // SAFETY: GetBuffer returned `frames` frames of writable device memory, valid until ReleaseBuffer.
    let out = unsafe { std::slice::from_raw_parts_mut(data, frames as usize * device.block) };
    let silent = {
        let mut state = shared.lock();
        let silent = if state.flushes == r.flushes {
            let plan = fill_period(
                &mut state.ring,
                &mut r.timeline,
                &mut r.sync,
                out,
                frames,
                device.block,
            );
            if plan.underrun > 0 {
                state.stats.underruns = state.stats.underruns.saturating_add(1);
                state.stats.silence_frames += u64::from(plan.underrun);
            }
            plan.is_silent()
        } else {
            // A flush is pending: the next pass resets the stream anyway.
            out.fill(0);
            true
        };
        state.stats.frames_rendered += u64::from(frames);
        state.stats.events += 1;
        silent
    };
    let flags = if silent {
        AUDCLNT_BUFFERFLAGS_SILENT.0 as u32
    } else {
        0
    };
    // SAFETY: releases exactly the buffer GetBuffer returned, fully written.
    unsafe { device.render.ReleaseBuffer(frames, flags) }.map_err(code)
}

/// Copies the render thread's view into the shared counters. Before the first position reading (after
/// create or a flush) the position is 0 and nothing is timed; while paused it stays where it stopped.
fn publish(shared: &Shared, r: &Render) {
    let mut state = shared.lock();
    let stats = &mut state.stats;
    stats.status = if r.started {
        STATUS_RUNNING
    } else if r.ever_started {
        STATUS_PAUSED
    } else {
        STATUS_STOPPED
    };
    let sample = r.sample.unwrap_or_default();
    stats.device_position_100ns = sample.hns;
    stats.device_qpc_100ns = sample.qpc_hns;
    stats.device_pts_100ns = r
        .sample
        .and_then(|sample| r.timeline.pts_at(sample.frames))
        .unwrap_or(PTS_NONE);
    let counters = &r.sync.counters;
    stats.sync_error_100ns = counters.last_error_hns;
    stats.syncs = counters.measured;
    stats.syncs_unmeasured = counters.unmeasured;
    stats.drop_corrections = counters.drop_corrections;
    stats.pad_corrections = counters.pad_corrections;
    stats.dropped_frames = counters.dropped_frames;
    stats.padded_frames = counters.padded_frames;
}

#[cfg(test)]
mod tests {
    use super::super::CONSOLE_PCM_7_1;
    use super::*;

    fn create_with(
        module: HMODULE,
        format: Option<&BaleAudioFormat>,
        ring_ms: u32,
    ) -> (i32, u32, BaleAudioOutOpenInfo) {
        let mut id = 7u32;
        let mut info = BaleAudioOutOpenInfo::default();
        let format = format.map_or(std::ptr::null(), std::ptr::from_ref);
        // SAFETY: valid locals; the module is null or this process's own.
        let hr = unsafe { bale_audio_out_create(module, format, ring_ms, &mut id, &mut info) };
        (hr, id, info)
    }

    /// Refusals that need no audio device: bad arguments and a missing mmdevapi.dll.
    #[test]
    fn create_refuses_bad_arguments_before_touching_the_device() {
        let bad = BaleAudioFormat {
            channels: 0,
            ..CONSOLE_PCM_7_1
        };
        let (hr, id, info) = create_with(HMODULE::default(), Some(&bad), 0);
        assert_eq!(
            (hr, id, info.step, info.hr),
            (E_INVALIDARG, 0, STEP_ARGUMENTS, E_INVALIDARG)
        );
        let (hr, _, info) = create_with(HMODULE::default(), Some(&CONSOLE_PCM_7_1), 49);
        assert_eq!((hr, info.step), (E_INVALIDARG, STEP_ARGUMENTS));
        let (hr, _, info) = create_with(HMODULE::default(), None, 0);
        assert_eq!((hr, info.step), (E_POINTER, STEP_ARGUMENTS));
        // No module: refused like the probe refuses it, before any thread or COM call.
        let (hr, id, info) = create_with(HMODULE::default(), Some(&CONSOLE_PCM_7_1), 0);
        assert_eq!((hr, id, info.step), (E_INVALIDARG, 0, STEP_MODULE));
        // SAFETY: a null id slot is refused before anything is written.
        let hr = unsafe {
            bale_audio_out_create(
                HMODULE::default(),
                &CONSOLE_PCM_7_1,
                0,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            )
        };
        assert_eq!(hr, E_POINTER);
    }

    #[test]
    fn unknown_ids_name_nothing() {
        for id in [0, 1, 65_535, u32::MAX] {
            // SAFETY: a valid buffer; unknown ids never read it.
            let written = unsafe { bale_audio_out_write(id, [0u8; 32].as_ptr().cast(), 32, 0) };
            assert_eq!(written, WRITE_CLOSED);
            assert_eq!(bale_audio_out_start(id), E_HANDLE);
            assert_eq!(bale_audio_out_pause(id), E_HANDLE);
            assert_eq!(bale_audio_out_flush(id), E_HANDLE);
            assert_eq!(bale_audio_out_sync(id, 0, 1), E_HANDLE);
            assert_eq!(bale_audio_out_sync(id, 0, 0), E_HANDLE);
            assert_eq!(bale_audio_out_destroy(id), E_HANDLE);
            let mut stats = BaleAudioOutStats::default();
            // SAFETY: valid out-slots.
            unsafe {
                assert_eq!(bale_audio_out_stats(id, &mut stats), E_HANDLE);
                assert_eq!(bale_audio_out_stats(id, std::ptr::null_mut()), E_POINTER);
                let mut format = BaleAudioFormat::default();
                assert_eq!(bale_audio_out_format(id, &mut format), E_HANDLE);
                assert_eq!(bale_audio_out_format(id, std::ptr::null_mut()), E_POINTER);
            }
        }
    }

    #[test]
    fn the_qpc_frequency_is_known() {
        assert!(qpc_frequency() > 0);
    }
}
