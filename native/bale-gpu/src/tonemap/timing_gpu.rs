//! Stage GPU time from two D3D11 event queries whose completion a poll thread sees on the CPU.
//!
//! Why not timestamps (2026-11-24): with TIMESTAMP / TIMESTAMP_DISJOINT queries, Bale crashed on
//! the Xbox on every measured playback (HEVC d3d11va and AV1 rav1g), about 3 s into the stage. The
//! full dump (Bale 0.1.762) shows EXCEPTION_BREAKPOINT (int 3) in the AMD D3D12 driver
//! (umd12ddi_arden.dll) on a d3d11on12.dll worker thread: the console's D3D11 is d3d11on12. In the
//! public D3D11On12 / D3D12TranslationLayer sources, a timestamp or disjoint query made on the
//! immediate context covers every command list type the device has created. On Media
//! Foundation's device that includes the video decode/process lists of d3d11va and MF's video
//! processing, so each timestamp also recorded EndQuery + ResolveQueryData on those lists, and the
//! disjoint query asked the video queues for a timestamp frequency: the most likely trigger of the
//! driver's break. It fits the grain probe, whose timestamps work on a private device that never
//! does video work.
//!
//! An event query records no GPU command: d3d11on12 completes it from fence values. Both events
//! are created for D3D11_CONTEXT_TYPE_3D (ID3D11Device3::CreateQuery1) so they follow the graphics
//! queue the stage runs on. An all-queue event would also wait for the newest decode, often a later
//! frame running alongside the stage, and could hide stage time instead of bounding it.
//!
//! Begin ends the start event just before the stage's first command (the optional input copy), end
//! ends the end event after the last draw. Right after each End, one GetData without
//! D3D11_ASYNC_GETDATA_DONOTFLUSH is the flush: D3D11's documented flush for an unfinished query.
//! On d3d11on12 it hands the batch to the worker thread with a flush request and returns, while
//! ID3D11DeviceContext::Flush waits for that thread to go idle, so apply never calls Flush. An
//! event on d3d11on12 completes with the command list it was ended in; the start's flush thus also
//! gives the stage a command list of its own. A poll thread then reads both events with DONOTFLUSH
//! every POLL_INTERVAL_US under the device's multithread lock, with QueryPerformanceCounter around
//! each read (`accounting::Flight` turns the reads into conservative edges).
//!
//! Accuracy: an upper bound of the stage's elapsed GPU time. Event completion includes queueing
//! behind other GPU work (on the d3d11va route also the wait for a frame decoded just before), up
//! to a poll period at each edge, and, when the GPU was idle at the start, the CPU's recording and
//! d3d11on12's submission of the stage. Idle-state readings with little other GPU work are the
//! closest. The poll thread cannot read while apply holds the device lock, so boundaries between
//! the passes would mostly measure CPU recording: only the total is reported.
//!
//! Rules: the immediate context only; one set in flight, never ended again before it completed
//! and was accounted; every failure disables timing once and the backend is never touched again;
//! apply never waits for the GPU. The poll thread never holds the mailbox while it takes the device
//! lock or sleeps, checks `stop` after taking the device lock, and is never joined (destroy may run
//! under the device lock the thread waits for): it drops its own references when it exits.
use super::timing::{
    self,
    accounting::{
        Failure, FailureReason, Flight, Mailbox, Outcome, POLL_INTERVAL_US, Report, Sample,
        data_size, event_state,
    },
};
use std::{
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Condvar, Mutex, MutexGuard, PoisonError,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use windows::{
    Win32::{
        Foundation::{E_FAIL, E_INVALIDARG, E_UNEXPECTED},
        Graphics::{Direct3D10::ID3D10Multithread, Direct3D11::*},
        System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
    },
    core::{BOOL, Interface},
};

type TimingResult<T> = std::result::Result<T, Failure>;

fn failure(reason: FailureReason, error: windows::core::Error) -> Failure {
    Failure::new(reason, error.code().0)
}
fn sequence_failure() -> Failure {
    Failure::new(FailureReason::Sequence, E_FAIL.0)
}

fn qpc() -> TimingResult<u64> {
    let mut ticks = 0i64;
    // SAFETY: local output.
    unsafe { QueryPerformanceCounter(&mut ticks) }
        .map_err(|e| failure(FailureReason::Frequency, e))?;
    u64::try_from(ticks).map_err(|_| Failure::new(FailureReason::Frequency, E_FAIL.0))
}

fn qpc_frequency() -> TimingResult<u64> {
    let mut frequency = 0i64;
    // SAFETY: local output.
    unsafe { QueryPerformanceFrequency(&mut frequency) }
        .map_err(|e| failure(FailureReason::Frequency, e))?;
    match u64::try_from(frequency) {
        Ok(frequency) if frequency > 0 => Ok(frequency),
        _ => Err(Failure::new(FailureReason::Frequency, E_FAIL.0)),
    }
}

/// One graphics-queue event; verify what the driver actually created.
fn event(device: &ID3D11Device3) -> TimingResult<ID3D11Query1> {
    let desc = D3D11_QUERY_DESC1 {
        Query: D3D11_QUERY_EVENT,
        MiscFlags: 0,
        ContextType: D3D11_CONTEXT_TYPE_3D,
    };
    let mut result = None;
    // SAFETY: initialized descriptor and local COM output; live same-device owner.
    unsafe { device.CreateQuery1(&desc, Some(&mut result)) }
        .map_err(|e| failure(FailureReason::Create, e))?;
    let query = result.ok_or(Failure::new(FailureReason::Create, E_FAIL.0))?;
    // SAFETY: live query; metadata only.
    let actual = unsafe { query.GetDesc1() };
    if actual.Query != desc.Query
        || actual.MiscFlags != desc.MiscFlags
        || actual.ContextType != desc.ContextType
    {
        return Err(Failure::new(FailureReason::Create, E_INVALIDARG.0));
    }
    // SAFETY: live query metadata. An event's data is one BOOL.
    data_size(unsafe { query.GetDataSize() }, size_of::<BOOL>())?;
    Ok(query)
}

/// Ok(true) once the event completed. `flags` is 0 only for the read right after End (the flush);
/// every poll passes D3D11_ASYNC_GETDATA_DONOTFLUSH. Caller holds the device's multithread lock.
fn event_done(
    context: &ID3D11DeviceContext,
    query: &ID3D11Query1,
    flags: u32,
) -> TimingResult<bool> {
    // Validate the driver's size BEFORE a pointer is passed, on every read as well as creation.
    // SAFETY: live query metadata, no output buffer yet.
    data_size(unsafe { query.GetDataSize() }, size_of::<BOOL>())?;
    let mut done = BOOL(0);
    // SAFETY: same-device query and matching initialized output, under the device lock.
    // The typed wrapper folds S_FALSE into Ok(()), so preserve the raw HRESULT here.
    let hr = unsafe {
        (context.vtable().GetData)(
            context.as_raw(),
            query.as_raw(),
            std::ptr::from_mut(&mut done).cast(),
            size_of::<BOOL>() as u32,
            flags,
        )
    };
    event_state(hr.0, done.0)
}

/// The apply side ↔ poll thread hand-off.
#[derive(Default)]
struct Shared {
    mailbox: Mutex<Mailbox>,
    wake: Condvar,
    stop: AtomicBool,
}
impl Shared {
    fn mailbox(&self) -> MutexGuard<'_, Mailbox> {
        self.mailbox.lock().unwrap_or_else(PoisonError::into_inner)
    }
    fn stopped(&self) -> bool {
        self.stop.load(Ordering::Acquire)
    }
    fn stop(&self) {
        let _mailbox = self.mailbox();
        self.stop.store(true, Ordering::Release);
        self.wake.notify_all();
    }
    fn post(&self, outcome: Outcome) {
        self.mailbox().outcome = Some(outcome);
    }
    /// Poll thread: the next measurement, or None once stopped.
    fn next_flight(&self) -> Option<Flight> {
        let mut mailbox = self.mailbox();
        loop {
            if self.stopped() {
                return None;
            }
            if let Some(flight) = mailbox.flight.take() {
                return Some(flight);
            }
            mailbox = self
                .wake
                .wait(mailbox)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }
}

/// The poll thread's own references; they keep the device alive until the thread exits.
struct PollBackend {
    context: ID3D11DeviceContext,
    multithread: ID3D10Multithread,
    start: ID3D11Query1,
    end: ID3D11Query1,
}

struct DeviceLock<'a>(&'a ID3D10Multithread);
impl<'a> DeviceLock<'a> {
    fn enter(multithread: &'a ID3D10Multithread) -> Self {
        // SAFETY: live interface; the returned guard pairs Enter/Leave on this thread.
        unsafe { multithread.Enter() };
        Self(multithread)
    }
}
impl Drop for DeviceLock<'_> {
    fn drop(&mut self) {
        // SAFETY: paired with this guard's Enter, including during unwinding.
        unsafe { self.0.Leave() };
    }
}

fn poll_thread(shared: &Shared, backend: &PollBackend) {
    while let Some(mut flight) = shared.next_flight() {
        let Some(result) = poll(shared, backend, &mut flight) else {
            return;
        };
        let failed = result.is_err();
        shared.post(match result {
            Ok(sample) => Outcome::Measured(sample),
            Err(failure) => Outcome::Failed(failure),
        });
        if failed {
            return; // never touch the backend again
        }
    }
}

/// Some once the measurement finished or failed; None once stopped.
fn poll(
    shared: &Shared,
    backend: &PollBackend,
    flight: &mut Flight,
) -> Option<TimingResult<Sample>> {
    loop {
        let polled = {
            let _lock = DeviceLock::enter(&backend.multithread);
            // Apply-side failures set it under the device lock, so no read follows one.
            if shared.stopped() {
                return None;
            }
            poll_once(backend, flight)
        };
        match polled {
            Ok(None) => std::thread::sleep(Duration::from_micros(POLL_INTERVAL_US)),
            Ok(Some(sample)) => return Some(Ok(sample)),
            Err(failure) => return Some(Err(failure)),
        }
    }
}

fn poll_once(backend: &PollBackend, flight: &mut Flight) -> TimingResult<Option<Sample>> {
    let flags = D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32;
    if !flight.start_done() {
        let before = qpc()?;
        let done = event_done(&backend.context, &backend.start, flags)?;
        flight.start_polled(before, done);
    }
    let before = qpc()?;
    let done = event_done(&backend.context, &backend.end, flags)?;
    let after = qpc()?;
    flight.end_polled(before, after, done).transpose()
}

/// The two events and their poll thread, owned by the stage, never its textures.
struct Events {
    start: ID3D11Query1,
    end: ID3D11Query1,
    frequency: u64,
    shared: Arc<Shared>,
    /// begin → end: QPC after the start's flush, and whether that read saw it complete.
    issued: Option<(u64, bool)>,
}
impl Events {
    fn create(device: &ID3D11Device) -> TimingResult<Self> {
        let frequency = qpc_frequency()?;
        let device3: ID3D11Device3 = device
            .cast()
            .map_err(|e| failure(FailureReason::Create, e))?;
        let start = event(&device3)?;
        let end = event(&device3)?;
        // SAFETY: live device; returns its immediate context with a new reference.
        let context = unsafe { device.GetImmediateContext() }
            .map_err(|e| failure(FailureReason::Poller, e))?;
        let multithread: ID3D10Multithread = context
            .cast()
            .map_err(|e| failure(FailureReason::Poller, e))?;
        // The poll thread shares the immediate context with MF's threads: only safe while D3D
        // serializes its calls. MF's device is protected; check, never assume.
        // SAFETY: live interface, metadata only.
        if !unsafe { multithread.GetMultithreadProtected() }.as_bool() {
            return Err(Failure::new(FailureReason::Poller, E_UNEXPECTED.0));
        }
        let shared = Arc::new(Shared::default());
        let backend = PollBackend {
            context,
            multithread,
            start: start.clone(),
            end: end.clone(),
        };
        let thread_shared = Arc::clone(&shared);
        let thread = std::thread::Builder::new()
            .name("bale-stage-timing".into())
            .spawn(move || {
                let polled =
                    catch_unwind(AssertUnwindSafe(|| poll_thread(&thread_shared, &backend)));
                if polled.is_err() {
                    thread_shared.post(Outcome::Failed(Failure::new(
                        FailureReason::Panic,
                        E_UNEXPECTED.0,
                    )));
                }
            })
            .map_err(|_| Failure::new(FailureReason::Poller, E_FAIL.0))?;
        drop(thread); // detached: never joined (see Drop)
        Ok(Self {
            start,
            end,
            frequency,
            shared,
            issued: None,
        })
    }

    /// Apply side: a poll-thread failure at once, a measurement once `ready`.
    fn take(&self, ready: bool) -> TimingResult<Option<Sample>> {
        self.shared.mailbox().take(ready)
    }

    fn issue_start(&mut self, context: &ID3D11DeviceContext) -> TimingResult<()> {
        if self.issued.is_some() {
            return Err(sequence_failure());
        }
        // SAFETY: event End (an event has no Begin) on the immediate context, under the caller's
        // lock; the previous measurement completed and was accounted before this set is reused.
        unsafe { context.End(&self.start) };
        let done = event_done(context, &self.start, 0)?;
        self.issued = Some((qpc()?, done));
        Ok(())
    }

    fn issue_end(&mut self, context: &ID3D11DeviceContext) -> TimingResult<()> {
        let (issued, start_done) = self.issued.take().ok_or_else(sequence_failure)?;
        let before = qpc()?;
        // SAFETY: as in issue_start; the stage's last draw is already recorded.
        unsafe { context.End(&self.end) };
        let done = event_done(context, &self.end, 0)?;
        let after = qpc()?;
        let mut flight = Flight::new(issued, start_done, before, self.frequency);
        let finished = flight.end_polled(before, after, done).transpose()?;
        let mut mailbox = self.shared.mailbox();
        if mailbox.flight.is_some() || mailbox.outcome.is_some() {
            return Err(sequence_failure());
        }
        match finished {
            Some(sample) => mailbox.outcome = Some(Outcome::Measured(sample)),
            None => {
                mailbox.flight = Some(flight);
                self.shared.wake.notify_one();
            }
        }
        Ok(())
    }
}
impl Drop for Events {
    fn drop(&mut self) {
        // No wait: the thread sees the flag at its next device-lock entry or hand-off and exits.
        self.shared.stop();
    }
}

pub(super) struct Timing {
    device: ID3D11Device,
    events: Option<Events>,
    report: Report,
}
impl Timing {
    /// Called after the create self-test. Disabled instances stay latched; never retry per frame.
    pub fn create(device: &ID3D11Device) -> Option<Self> {
        let stats = timing::start()?;
        let mut timing = Self {
            device: device.clone(),
            events: None,
            report: Report::new(stats),
        };
        timing.attempt(None, |events, _| {
            *events = Some(Events::create(device)?);
            Ok(())
        });
        Some(timing)
    }
    fn attempt<T>(
        &mut self,
        context: Option<&ID3D11DeviceContext>,
        operation: impl FnOnce(&mut Option<Events>, &mut Report) -> TimingResult<T>,
    ) -> Option<T> {
        let previous = (self.report.stats.state, self.report.stats.window);
        let result = self.report.attempt(|report| {
            if let Some(context) = context {
                // SAFETY: metadata on live same-device interfaces. End is void; device status is
                // the only HRESULT it exposes indirectly. Never read a deferred context.
                if unsafe { context.GetType() } != D3D11_DEVICE_CONTEXT_IMMEDIATE {
                    return Err(Failure::new(FailureReason::Context, E_INVALIDARG.0));
                }
            }
            unsafe { self.device.GetDeviceRemovedReason() }
                .map_err(|e| failure(FailureReason::Device, e))?;
            let value = operation(&mut self.events, report)?;
            unsafe { self.device.GetDeviceRemovedReason() }
                .map_err(|e| failure(FailureReason::Device, e))?;
            Ok(value)
        });
        if self.report.stats.state == 2 {
            // Stops the poll thread while apply still holds the device lock: no read follows.
            self.events = None;
        }
        if previous != (self.report.stats.state, self.report.stats.window) {
            timing::publish(self.report.stats);
        }
        result
    }
    /// Just before the stage's first command; Some(slot) when this apply is measured.
    pub fn begin(&mut self, context: &ID3D11DeviceContext) -> Option<usize> {
        self.attempt(Some(context), |events, report| {
            let events = events.as_mut().ok_or_else(sequence_failure)?;
            report.ring.advance();
            if let Some(sample) = events.take(report.ring.ready_to_poll(0))? {
                if !report.ring.release(0) {
                    return Err(sequence_failure());
                }
                report.record(sample);
            }
            let Some(slot) = report.ring.reserve() else {
                report.record(Sample::Unavailable);
                return Ok(None); // Still in flight: skip this measurement, never reuse the set.
            };
            events.issue_start(context)?;
            Ok(Some(slot))
        })
        .flatten()
    }
    /// Just after the stage's last draw of a measured apply.
    pub fn end(&mut self, context: &ID3D11DeviceContext, slot: usize) {
        self.attempt(Some(context), |events, _| {
            if slot != 0 {
                return Err(sequence_failure());
            }
            events
                .as_mut()
                .ok_or_else(sequence_failure)?
                .issue_end(context)
        });
    }
}
impl Drop for Timing {
    fn drop(&mut self) {
        // No query calls or waits at teardown: account what the poll thread already handed back,
        // stop it, and retire a measurement still in flight as unavailable (close).
        if let Some(events) = self.events.take() {
            let taken = events.take(true);
            drop(events);
            match taken {
                Ok(Some(sample)) => {
                    if self.report.ring.release(0) {
                        self.report.record(sample);
                    }
                }
                Ok(None) => {}
                Err(failure) => {
                    self.report.attempt::<()>(|_| Err(failure));
                }
            }
        }
        self.report.close();
        timing::publish(self.report.stats);
    }
}
