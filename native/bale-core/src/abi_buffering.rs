//! The handle ABI for the buffering-quality controller and its signal mapper (`buffering.rs`).
//!
//! The controller crosses in JSON, like the decision calls: it runs on player events, not per
//! frame. `bale_core_buffering_call` takes one event with the host clock's time, the settings and
//! the measured throughput, and returns the ordered steps it caused (timers to schedule or cancel,
//! outputs to raise), the state the host reads between calls, and the event's own answer. The
//! signal mapper runs on every UI tick, so it takes a bit set and returns packed calls, no JSON.
//!
//! Both handles own a `Mutex`; a panic is caught (the controller call then fails with a message, a
//! signals call does nothing).

use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::abi::{CALL_FAILED, CALL_INVALID_ARGUMENT, CALL_OK, bytes, error_body};
use crate::abi_meters::{create, destroy, with};
use crate::buffering::{
    Controller, Inputs, Mode, Pill, Plan, Replan, Signals, Snapshot, Step, Summary, Timer,
    WaitCause,
};

#[derive(Deserialize)]
struct Call {
    now: i64,
    mode: Mode,
    sends_fields: bool,
    measured_kbps: Option<i32>,
    event: Event,
}

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Event {
    PlanAdopted {
        plan: Plan,
        controller_initiated: bool,
    },
    PlanEnded,
    NewAttempt,
    Playing,
    Waiting {
        cause: WaitCause,
        started_ago_ms: i64,
    },
    Paused,
    Seeked,
    TransportTrouble {
        trouble: bool,
    },
    ReplanFailed,
    ReplanRefused,
    StartDeadlinePassed,
    SelectLower,
    SelectKeep,
    SelectRestore,
    SelectPrimary,
    Back,
    QualityChanged,
    PickSent,
    PickRefused,
    CompetingPillVisible {
        visible: bool,
    },
    Timer {
        timer: Timer,
    },
    ClientReplan {
        operation: String,
    },
    RunningSummary,
}

#[derive(Serialize)]
struct State<'a> {
    pill: Option<Pill>,
    pill_pending: bool,
    kept_estimate_kbps: Option<i32>,
    current_plan_id: Option<&'a str>,
}

#[derive(Serialize)]
struct Outcome<'a> {
    steps: &'a [Step],
    state: State<'a>,
    /// `StartDeadlinePassed` and `Back`.
    returned: Option<bool>,
    /// `ClientReplan`.
    replan: Option<Replan>,
    /// `RunningSummary`.
    summary: Option<Summary>,
}

/// Runs one event and writes the outcome.
pub fn run(controller: &mut Controller, input: &[u8]) -> Result<Vec<u8>, String> {
    // serde_json's messages quote unexpected input, so report only where the JSON went wrong.
    let call: Call = serde_json::from_slice(input).map_err(|error| {
        format!(
            "buffering: event is not valid JSON (line {}, column {})",
            error.line(),
            error.column()
        )
    })?;
    controller.clear_steps();
    controller.set_inputs(Inputs {
        now: call.now,
        mode: call.mode,
        sends_fields: call.sends_fields,
        measured_kbps: call.measured_kbps,
    });
    let mut returned = None;
    let mut replan = None;
    let mut summary = None;
    match call.event {
        Event::PlanAdopted {
            plan,
            controller_initiated,
        } => controller.plan_adopted(plan, controller_initiated),
        Event::PlanEnded => controller.plan_ended(),
        Event::NewAttempt => controller.new_attempt(),
        Event::Playing => controller.playing(),
        Event::Waiting {
            cause,
            started_ago_ms,
        } => controller.waiting(cause, started_ago_ms),
        Event::Paused => controller.paused(),
        Event::Seeked => controller.seeked(),
        Event::TransportTrouble { trouble } => controller.transport_trouble_changed(trouble),
        Event::ReplanFailed => controller.replan_failed(),
        Event::ReplanRefused => controller.replan_refused(),
        Event::StartDeadlinePassed => returned = Some(controller.start_deadline_passed()),
        Event::SelectLower => controller.select_lower(),
        Event::SelectKeep => controller.select_keep(),
        Event::SelectRestore => controller.select_restore(),
        Event::SelectPrimary => controller.select_primary(),
        Event::Back => returned = Some(controller.back()),
        Event::QualityChanged => controller.quality_changed(),
        Event::PickSent => controller.pick_sent(),
        Event::PickRefused => controller.pick_refused(),
        Event::CompetingPillVisible { visible } => controller.set_competing_pill_visible(visible),
        Event::Timer { timer } => controller.timer_fired(timer),
        Event::ClientReplan { operation } => replan = controller.client_replan(operation),
        Event::RunningSummary => summary = controller.running_summary(),
    }
    let outcome = Outcome {
        steps: controller.steps(),
        state: State {
            pill: controller.pill(),
            pill_pending: controller.pill_pending(),
            kept_estimate_kbps: controller.kept_estimate_kbps(),
            current_plan_id: controller.current_plan_id(),
        },
        returned,
        replan,
        summary,
    };
    let body = serde_json::to_vec(&outcome)
        .map_err(|_| "buffering: outcome could not be serialised".to_owned());
    controller.clear_steps();
    body
}

// ---- Controller ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_buffering_create() -> *mut Mutex<Controller> {
    create(Controller::new())
}

/// # Safety
/// A handle from `bale_core_buffering_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_buffering_destroy(handle: *mut Mutex<Controller>) {
    unsafe { destroy(handle) }
}

/// Runs one JSON event on the controller and hands back a buffer the caller must release with
/// `bale_core_free` (on `CALL_OK`, the outcome; on `CALL_FAILED`, a JSON string saying why).
///
/// # Safety
///
/// A live handle (or null); `input` points to `input_len` readable bytes (dangling when 0), and
/// `output` and `output_len` are valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_buffering_call(
    handle: *mut Mutex<Controller>,
    input: *const u8,
    input_len: usize,
    output: *mut *mut u8,
    output_len: *mut usize,
) -> i32 {
    if handle.is_null() || output.is_null() || output_len.is_null() {
        return CALL_INVALID_ARGUMENT;
    }
    // SAFETY: the caller guarantees the range is readable; a zero length never reads.
    let Some(input) = (unsafe { bytes(input, input_len) }) else {
        return CALL_INVALID_ARGUMENT;
    };
    let result = unsafe {
        with(
            handle,
            Err("buffering: the call panicked".to_owned()),
            |c| run(c, input),
        )
    };
    let (status, body) = match result {
        Ok(body) => (CALL_OK, body),
        Err(message) => (CALL_FAILED, error_body(&message)),
    };
    let len = body.len();
    let body = Box::into_raw(body.into_boxed_slice());
    // SAFETY: checked non-null above; the caller guarantees they are writable.
    unsafe {
        *output_len = len;
        *output = body.cast();
    }
    status
}

// ---- Signals ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_signals_create() -> *mut Mutex<Signals> {
    create(Signals::new())
}

/// # Safety
/// A handle from `bale_core_signals_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_destroy(handle: *mut Mutex<Signals>) {
    unsafe { destroy(handle) }
}

/// Maps one snapshot (bits as [`Snapshot::from_bits`]) to at most two calls, packed one per byte
/// from the lowest: the call's `Signal` value + 1, 0 for none.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_update(handle: *mut Mutex<Signals>, bits: u32) -> u32 {
    unsafe {
        with(handle, 0, |signals| {
            let (calls, count) = signals.update(Snapshot::from_bits(bits));
            calls[..count]
                .iter()
                .flatten()
                .enumerate()
                .fold(0, |packed, (index, &call)| {
                    packed | ((call as u32 + 1) << (8 * index))
                })
        })
    }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_plan_adopted(handle: *mut Mutex<Signals>) {
    unsafe { with(handle, (), Signals::plan_adopted) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_invalidate(handle: *mut Mutex<Signals>) {
    unsafe { with(handle, (), Signals::invalidate) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_note_request_failed(handle: *mut Mutex<Signals>) {
    unsafe { with(handle, (), Signals::note_media_request_failed) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_note_data_arrived(handle: *mut Mutex<Signals>) {
    unsafe { with(handle, (), Signals::note_media_data_arrived) }
}

/// 1 when the adopted plan has run since its adoption.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_signals_plan_has_run(handle: *mut Mutex<Signals>) -> u8 {
    unsafe { with(handle, 0, |signals| u8::from(signals.plan_has_run())) }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn call(controller: &mut Controller, json: &str) -> serde_json::Value {
        serde_json::from_slice(&run(controller, json.as_bytes()).unwrap()).unwrap()
    }

    #[test]
    fn an_event_returns_its_steps_and_the_state() {
        let mut c = Controller::new();
        let adopted = call(
            &mut c,
            r#"{"now":0,"mode":"always","sends_fields":true,"measured_kbps":1,"event":{"type":"plan_adopted","controller_initiated":true,
                "plan":{"id":"1","delivered_kbps":63000,"quality":"auto","available_qualities":[{"label":"1080p","bitrate_kbps":10000}],
                "quality_reduction_unavailable":false,"active_quality":null,"requested_bandwidth_estimate_kbps":null}}}"#,
        );
        assert_eq!(adopted["state"]["current_plan_id"], "1");
        assert_eq!(adopted["steps"], serde_json::json!([]));
        call(
            &mut c,
            r#"{"now":0,"mode":"always","sends_fields":true,"measured_kbps":1,"event":{"type":"playing"}}"#,
        );
        let waiting = call(
            &mut c,
            r#"{"now":20000,"mode":"always","sends_fields":true,"measured_kbps":1,"event":{"type":"waiting","cause":"network","started_ago_ms":0}}"#,
        );
        assert_eq!(
            waiting["steps"],
            serde_json::json!([{"step":"schedule","timer":"long_stall","at":26000}])
        );
        let fired = call(
            &mut c,
            r#"{"now":26000,"mode":"always","sends_fields":true,"measured_kbps":1,"event":{"type":"timer","timer":"long_stall"}}"#,
        );
        // A lowering after the first frame carries the slow-link claim with the figure it rests on.
        assert_eq!(
            fired["steps"],
            serde_json::json!([
                {"step":"schedule","timer":"rearm","at":86000},
                {"step":"replan","replan":{"operation":"quality_change","quality_preference":"auto","bandwidth_estimate_kbps":44100,
                    "bandwidth_estimate_source":"observed_rebuffer","learned_limit":null,"observed_throughput_kbps":1}}
            ])
        );
        assert_eq!(fired["state"]["kept_estimate_kbps"], 44100);
        let ended = call(
            &mut c,
            r#"{"now":30000,"mode":"always","sends_fields":true,"measured_kbps":null,"event":{"type":"plan_ended"}}"#,
        );
        assert_eq!(
            ended["steps"][0],
            serde_json::json!({"step":"cancel","timer":"rearm"})
        );
        assert_eq!(ended["steps"][1]["summary"]["action"], "lowered");
        assert_eq!(ended["steps"][1]["summary"]["stall_total_ms"], 10000);
        assert_eq!(ended["state"]["current_plan_id"], serde_json::Value::Null);
    }

    #[test]
    fn bad_json_fails_without_quoting_it() {
        let error = run(&mut Controller::new(), b"{\"secret\"").unwrap_err();
        assert!(!error.contains("secret"));
    }

    #[test]
    fn signals_pack_trouble_before_the_media_call() {
        let handle = bale_core_signals_create();
        unsafe {
            bale_core_signals_plan_adopted(handle);
            assert_eq!(bale_core_signals_plan_has_run(handle), 0);
            // Playing: Signal::Playing (2) + 1.
            assert_eq!(bale_core_signals_update(handle, 1 | 2 | 4), 3);
            assert_eq!(bale_core_signals_plan_has_run(handle), 1);
            bale_core_signals_note_request_failed(handle);
            // TroubleStarted (0) + 1, then WaitingNetwork (3) + 1.
            assert_eq!(bale_core_signals_update(handle, 1 | 2), 1 | (4 << 8));
            assert_eq!(bale_core_signals_update(handle, 1 | 2), 0);
            bale_core_signals_invalidate(handle);
            assert_eq!(bale_core_signals_update(handle, 1 | 2), 4);
            bale_core_signals_destroy(handle);
            assert_eq!(bale_core_signals_update(std::ptr::null_mut(), 1), 0);
        }
    }
}
