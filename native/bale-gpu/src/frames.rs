//! Passive, native-only sample delivery observation. No sample or request survives its callback.

use core::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Mutex};

use windows::Foundation::TypedEventHandler;
use windows::Media::Core::{
    IMediaStreamDescriptor, MediaStreamSource, MediaStreamSourceSampleRequestedEventArgs,
    MediaStreamSourceStartingEventArgs, VideoStreamDescriptor,
};
use windows::Media::Playback::{MediaPlaybackSession, MediaPlaybackState};
use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_NOINTERFACE, E_POINTER};
use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
use windows::core::{Error, Interface, Result};

/// Blittable C ABI; timestamps, durations and positions are 100 ns TimeSpan ticks.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FrameRecord {
    pub kind: u32,
    pub flags: u32,
    pub now_ms: f64,
    pub timestamp: i64,
    pub duration: i64,
    pub position: i64,
}

// Deliberately independent of WinRT/COM so overflow and drain semantics have pure unit tests.
mod buffer {
    use std::collections::VecDeque;

    use super::FrameRecord;

    pub const CAPACITY: usize = 4096;

    pub struct Ring {
        records: VecDeque<FrameRecord>,
        pub dropped: u64,
    }

    impl Ring {
        pub fn new() -> Self {
            Self { records: VecDeque::with_capacity(CAPACITY), dropped: 0 }
        }

        pub fn push(&mut self, record: FrameRecord) {
            if self.records.len() == CAPACITY {
                self.records.pop_front();
                self.dropped = self.dropped.saturating_add(1);
            }
            self.records.push_back(record);
        }

        pub fn drain(&mut self, output: &mut [FrameRecord]) -> usize {
            let mut count = 0;
            for destination in output {
                let Some(record) = self.records.pop_front() else { break };
                *destination = record;
                count += 1;
            }
            count
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        fn record(timestamp: i64) -> FrameRecord {
            FrameRecord { kind: 1, timestamp, ..FrameRecord::default() }
        }

        #[test]
        fn record_layout_matches_the_csharp_abi() {
            assert_eq!(core::mem::size_of::<FrameRecord>(), 40);
            assert_eq!(core::mem::offset_of!(FrameRecord, kind), 0);
            assert_eq!(core::mem::offset_of!(FrameRecord, flags), 4);
            assert_eq!(core::mem::offset_of!(FrameRecord, now_ms), 8);
            assert_eq!(core::mem::offset_of!(FrameRecord, timestamp), 16);
            assert_eq!(core::mem::offset_of!(FrameRecord, duration), 24);
            assert_eq!(core::mem::offset_of!(FrameRecord, position), 32);
        }

        #[test]
        fn push_and_drain_preserve_order() {
            let mut ring = Ring::new();
            for timestamp in 0..3 { ring.push(record(timestamp)); }
            let mut output = [FrameRecord::default(); 4];
            assert_eq!(ring.drain(&mut output), 3);
            assert_eq!(output.get(..3), Some([record(0), record(1), record(2)].as_slice()));
            assert_eq!(ring.drain(&mut output), 0);
            assert_eq!(ring.dropped, 0);
        }

        #[test]
        fn overflow_drops_oldest_and_counts_all_losses() {
            let mut ring = Ring::new();
            for timestamp in 0..(CAPACITY + 3) { ring.push(record(timestamp as i64)); }
            let mut output = vec![FrameRecord::default(); CAPACITY];
            assert_eq!(ring.drain(&mut output), CAPACITY);
            assert_eq!(output.first(), Some(&record(3)));
            assert_eq!(output.last(), Some(&record((CAPACITY + 2) as i64)));
            assert_eq!(ring.dropped, 3);
            ring.push(record(9000));
            assert_eq!(ring.dropped, 3);
        }

        #[test]
        fn partial_and_empty_drains_leave_the_remaining_records() {
            let mut ring = Ring::new();
            for timestamp in 0..5 { ring.push(record(timestamp)); }
            assert_eq!(ring.drain(&mut []), 0);
            let mut output = [FrameRecord::default(); 2];
            assert_eq!(ring.drain(&mut output), 2);
            assert_eq!(output, [record(0), record(1)]);
            assert_eq!(ring.drain(&mut output), 2);
            assert_eq!(output, [record(2), record(3)]);
            assert_eq!(ring.drain(&mut output), 1);
            assert_eq!(output.first(), Some(&record(4)));
            assert_eq!(ring.dropped, 0);
        }
    }
}

struct State {
    ring: Mutex<buffer::Ring>,
    kinds: Mutex<Vec<(usize, bool)>>,
    video: Mutex<Option<VideoStreamDescriptor>>,
    // windows 0.62.2 explicitly implements both Send and Sync for MediaPlaybackSession.
    session: MediaPlaybackSession,
    frequency: i64,
    unavailable: AtomicBool,
    status: AtomicI32,
}

impl State {
    fn fail(&self, error: &Error) {
        let _ = self.status.compare_exchange(0, error.code().0, Ordering::AcqRel, Ordering::Acquire);
        self.unavailable.store(true, Ordering::Release);
    }

    fn now_ms(&self) -> Result<f64> {
        let mut counter = 0;
        // SAFETY: the out-pointer is a valid local; frequency was checked during create.
        unsafe { QueryPerformanceCounter(&mut counter)? };
        Ok(counter as f64 * (1000.0 / self.frequency as f64))
    }

    fn is_video(&self, descriptor: &IMediaStreamDescriptor) -> Result<bool> {
        let mut kinds = self.kinds.lock().map_err(|_| Error::from_hresult(E_FAIL))?;
        let pointer = descriptor.as_raw() as usize;
        if let Some((_, video)) = kinds.iter().find(|(known, _)| *known == pointer) {
            return Ok(*video);
        }
        let video = match descriptor.cast::<VideoStreamDescriptor>() {
            Ok(video) => {
                let mut first = self.video.lock().map_err(|_| Error::from_hresult(E_FAIL))?;
                if first.is_none() { *first = Some(video); }
                true
            }
            Err(error) if error.code() == E_NOINTERFACE => false,
            Err(error) => return Err(error),
        };
        if kinds.len() < 16 { kinds.push((pointer, video)); }
        Ok(video)
    }

    fn sample(&self, args: &MediaStreamSourceSampleRequestedEventArgs) -> Result<()> {
        // Markers, starts and samples share this gate, including their QPC timestamps. A seek cannot
        // land between observing a sample's clock/state and inserting its record in the timeline.
        let mut ring = self.ring.lock().map_err(|_| Error::from_hresult(E_FAIL))?;
        if self.unavailable.load(Ordering::Acquire) { return Ok(()) }
        let request = args.Request()?;
        // A null sample (or a sample getter error) means the upstream handler delivered nothing.
        let Ok(sample) = request.Sample() else { return Ok(()) };
        if !self.is_video(&request.StreamDescriptor()?)? { return Ok(()) }
        let timestamp = sample.Timestamp()?.Duration;
        let duration = sample.Duration()?.Duration;
        let discontinuous = sample.Discontinuous()?;
        let position = self.session.Position()?.Duration;
        let playing = self.session.PlaybackState()? == MediaPlaybackState::Playing;
        ring.push(FrameRecord {
            kind: 1,
            flags: u32::from(playing) | (u32::from(discontinuous) << 1),
            now_ms: self.now_ms()?,
            timestamp,
            duration,
            position,
        });
        // request, sample and descriptor references are all released here, never by the managed GC.
        Ok(())
    }

    fn mark(&self, kind: u32) -> Result<()> {
        let mut ring = self.ring.lock().map_err(|_| Error::from_hresult(E_FAIL))?;
        if self.unavailable.load(Ordering::Acquire) { return Ok(()) }
        ring.push(FrameRecord { kind, now_ms: self.now_ms()?, ..FrameRecord::default() });
        Ok(())
    }
}

struct Observer {
    source: MediaStreamSource,
    state: Arc<State>,
    sample_token: i64,
    starting_token: i64,
}

impl Drop for Observer {
    fn drop(&mut self) {
        self.state.unavailable.store(true, Ordering::Release);
        let _ = self.source.RemoveSampleRequested(self.sample_token);
        let _ = self.source.RemoveStarting(self.starting_token);
        // In-flight delegates own their Arc, so they can safely finish after unsubscription.
    }
}

fn create(source: MediaStreamSource, session: MediaPlaybackSession) -> Result<Observer> {
    let mut frequency = 0;
    // SAFETY: the out-pointer is a valid local.
    unsafe { QueryPerformanceFrequency(&mut frequency)? };
    if frequency <= 0 { return Err(Error::from_hresult(E_FAIL)) }
    let state = Arc::new(State {
        ring: Mutex::new(buffer::Ring::new()),
        kinds: Mutex::new(Vec::with_capacity(16)),
        video: Mutex::new(None),
        session,
        frequency,
        unavailable: AtomicBool::new(false),
        status: AtomicI32::new(0),
    });
    let starting_state = Arc::clone(&state);
    let starting = TypedEventHandler::<MediaStreamSource, MediaStreamSourceStartingEventArgs>::new(move |_, _| {
        if let Err(error) = starting_state.mark(2) { starting_state.fail(&error); }
        Ok(())
    });
    let starting_token = source.Starting(&starting)?;
    let sample_state = Arc::clone(&state);
    let sample = TypedEventHandler::<MediaStreamSource, MediaStreamSourceSampleRequestedEventArgs>::new(move |_, args| {
        if !sample_state.unavailable.load(Ordering::Acquire) {
            let result = args.ok().and_then(|args| sample_state.sample(args));
            if let Err(error) = result { sample_state.fail(&error); }
        }
        // Diagnostics must never fail the upstream delivery, even if their own COM call failed.
        Ok(())
    });
    let sample_token = match source.SampleRequested(&sample) {
        Ok(token) => token,
        Err(error) => {
            state.unavailable.store(true, Ordering::Release);
            let _ = source.RemoveStarting(starting_token);
            return Err(error);
        }
    };
    Ok(Observer { source, state, sample_token, starting_token })
}

/// Subscribes after FFmpeg's synchronous sample handler; takes its own references to both objects.
///
/// # Safety
/// `source` and `session` must be live borrowed default-interface pointers for MediaStreamSource and
/// MediaPlaybackSession. `out` must point to one writable pointer. Destroy the returned handle once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_create(source: *mut c_void, session: *mut c_void, out: *mut *mut c_void) -> i32 {
    if out.is_null() { return E_POINTER.0 }
    // SAFETY: caller guarantees a writable out-pointer; initialize it even on failure.
    unsafe { *out = core::ptr::null_mut() };
    // SAFETY: the caller guarantees live default-interface pointers; clones take our own references.
    let Some(source) = (unsafe { MediaStreamSource::from_raw_borrowed(&source) }).cloned() else { return E_POINTER.0 };
    // SAFETY: same borrowing contract as source above.
    let Some(session) = (unsafe { MediaPlaybackSession::from_raw_borrowed(&session) }).cloned() else { return E_POINTER.0 };
    match create(source, session) {
        Ok(observer) => {
            // SAFETY: out is writable; ownership of this allocation passes to destroy.
            unsafe { *out = Box::into_raw(Box::new(observer)).cast() };
            0
        }
        Err(error) => error.code().0,
    }
}

/// Inserts a seeking-on (3) or seeking-off (4) marker in sample order.
///
/// # Safety
/// `obs` must be null or a live observer handle, not concurrently being destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_mark(obs: *mut c_void, kind: u32) -> i32 {
    // SAFETY: guaranteed by the handle contract.
    let Some(observer) = (unsafe { obs.cast::<Observer>().as_ref() }) else { return E_POINTER.0 };
    if kind != 3 && kind != 4 { return E_INVALIDARG.0 }
    match observer.state.mark(kind) {
        Ok(()) => observer.state.status.load(Ordering::Acquire),
        Err(error) => { observer.state.fail(&error); error.code().0 }
    }
}

/// Copies the oldest records and reports the cumulative number discarded on overflow.
///
/// # Safety
/// `obs` must be null or a live handle, not concurrently destroyed. Non-null `records` must point to
/// `capacity` writable FrameRecords; non-null `dropped` must point to a writable u64. Outputs must not alias.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_drain(obs: *mut c_void, records: *mut FrameRecord, capacity: u32, dropped: *mut u64) -> u32 {
    // SAFETY: guaranteed by the handle contract.
    let Some(observer) = (unsafe { obs.cast::<Observer>().as_ref() }) else { return 0 };
    let Ok(mut ring) = observer.state.ring.lock() else {
        observer.state.fail(&Error::from_hresult(E_FAIL));
        return 0;
    };
    if !dropped.is_null() {
        // SAFETY: caller guarantees a writable non-null dropped pointer.
        unsafe { *dropped = ring.dropped };
    }
    if records.is_null() || capacity == 0 { return 0 }
    // SAFETY: caller guarantees capacity writable records, disjoint from dropped and the observer.
    let output = unsafe { core::slice::from_raw_parts_mut(records, capacity as usize) };
    ring.drain(output) as u32
}

/// Returns zero or the first observer failure HRESULT, without system error text.
///
/// # Safety
/// `obs` must be null or a live observer handle, not concurrently being destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_status(obs: *mut c_void) -> i32 {
    // SAFETY: guaranteed by the handle contract.
    match unsafe { obs.cast::<Observer>().as_ref() } {
        Some(observer) => observer.state.status.load(Ordering::Acquire),
        None => E_POINTER.0,
    }
}

/// Returns the first video descriptor's default interface with one owned COM reference, or null.
///
/// # Safety
/// `obs` must be null or a live handle, not concurrently destroyed. The caller must Release a non-null result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_video_descriptor(obs: *mut c_void) -> *mut c_void {
    // SAFETY: guaranteed by the handle contract.
    let Some(observer) = (unsafe { obs.cast::<Observer>().as_ref() }) else { return core::ptr::null_mut() };
    if let Ok(video) = observer.state.video.lock() {
        if let Some(video) = video.as_ref() { return video.clone().into_raw() }
    }
    core::ptr::null_mut()
}

/// Unsubscribes and releases the observer; null is a no-op.
///
/// # Safety
/// A non-null handle must have been returned by create, not already destroyed, and must have no
/// concurrent exported calls. In-flight event callbacks may finish using their independently owned Arcs.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_frame_observer_destroy(obs: *mut c_void) {
    if !obs.is_null() {
        // SAFETY: caller transfers the unique live allocation back to us.
        drop(unsafe { Box::from_raw(obs.cast::<Observer>()) });
    }
}
