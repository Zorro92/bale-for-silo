//! The handle ABI for Home's events-socket subscription (`home_events.rs`). It runs on socket events and timers, a
//! few times a minute at most, so it crosses in JSON like the buffering controller: one call takes the host clock's
//! time and one event, and returns the actions in order, when to tick next, and the state the host reads. The handle
//! owns a `Mutex`; a panic is caught and the call fails with a message.

use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::abi::{CALL_FAILED, CALL_INVALID_ARGUMENT, CALL_OK, bytes, error_body};
use crate::abi_meters::{create, destroy, with};
use crate::home_events::{Action, HomeEvents, Policy, Ticket};

#[derive(Deserialize)]
struct Call {
    now: i64,
    event: Event,
}

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Event {
    Start {
        profile_id: String,
        policy: Option<Policy>,
    },
    Tick,
    MintSucceeded {
        generation: u32,
        ticket: Option<String>,
        expires_in: Option<i64>,
        max_connection_seconds: Option<i64>,
        protocol: Option<String>,
    },
    MintFailed {
        generation: u32,
        status: Option<u16>,
        retry_after_ms: Option<i64>,
    },
    Opened {
        generation: u32,
        protocol: Option<String>,
    },
    ConnectFailed {
        generation: u32,
        status: Option<u16>,
    },
    Frame {
        generation: u32,
        text: String,
    },
    Closed {
        generation: u32,
        code: Option<u16>,
    },
}

#[derive(Serialize)]
struct Outcome<'a> {
    actions: &'a [Action],
    next_deadline: Option<i64>,
    live: bool,
    stopped: bool,
    notes: &'a [String],
}

/// Runs one event and writes the outcome.
pub fn run(machine: &mut HomeEvents, input: &[u8]) -> Result<Vec<u8>, String> {
    // serde_json's messages quote unexpected input (a frame, a ticket), so report only where the JSON went wrong.
    let call: Call = serde_json::from_slice(input).map_err(|error| {
        format!(
            "home events: event is not valid JSON (line {}, column {})",
            error.line(),
            error.column()
        )
    })?;
    machine.clear();
    let now = call.now;
    match call.event {
        Event::Start { profile_id, policy } => {
            machine.start(now, &profile_id, policy.unwrap_or(Policy::DEFAULT))
        }
        Event::Tick => machine.tick(now),
        Event::MintSucceeded {
            generation,
            ticket,
            expires_in,
            max_connection_seconds,
            protocol,
        } => machine.mint_succeeded(
            now,
            generation,
            &Ticket {
                ticket,
                expires_in,
                max_connection_seconds,
                protocol,
            },
        ),
        Event::MintFailed {
            generation,
            status,
            retry_after_ms,
        } => machine.mint_failed(now, generation, status, retry_after_ms),
        Event::Opened {
            generation,
            protocol,
        } => machine.opened(now, generation, protocol.as_deref()),
        Event::ConnectFailed { generation, status } => {
            machine.connect_failed(now, generation, status)
        }
        Event::Frame { generation, text } => machine.frame(now, generation, &text),
        Event::Closed { generation, code } => machine.closed(now, generation, code),
    }
    let outcome = Outcome {
        actions: machine.actions(),
        next_deadline: machine.next_deadline(),
        live: machine.live(),
        stopped: machine.stopped(),
        notes: machine.notes(),
    };
    let body = serde_json::to_vec(&outcome)
        .map_err(|_| "home events: outcome could not be serialised".to_owned());
    machine.clear();
    body
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_home_events_create() -> *mut Mutex<HomeEvents> {
    create(HomeEvents::new())
}

/// # Safety
/// A handle from `bale_core_home_events_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_home_events_destroy(handle: *mut Mutex<HomeEvents>) {
    unsafe { destroy(handle) }
}

/// Runs one JSON event and hands back a buffer the caller must release with `bale_core_free` (on `CALL_OK`, the
/// outcome; on `CALL_FAILED`, a JSON string saying why).
///
/// # Safety
///
/// A live handle (or null); `input` points to `input_len` readable bytes (dangling when 0), and `output` and
/// `output_len` are valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_home_events_call(
    handle: *mut Mutex<HomeEvents>,
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
            Err("home events: the call panicked".to_owned()),
            |machine| run(machine, input),
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::abi::bale_core_free;
    use serde_json::{Value, json};

    fn call(handle: *mut Mutex<HomeEvents>, input: &Value) -> (i32, Value) {
        let input = serde_json::to_vec(input).unwrap();
        let mut output = std::ptr::null_mut();
        let mut output_len = 0;
        unsafe {
            let status = bale_core_home_events_call(
                handle,
                input.as_ptr(),
                input.len(),
                &mut output,
                &mut output_len,
            );
            let body = std::slice::from_raw_parts(output, output_len).to_vec();
            bale_core_free(output, output_len);
            (status, serde_json::from_slice(&body).unwrap())
        }
    }

    #[test]
    fn a_session_crosses_in_json() {
        let handle = bale_core_home_events_create();
        let (status, outcome) = call(
            handle,
            &json!({"now": 0, "event": {"type": "start", "profile_id": "p1", "policy": null}}),
        );
        assert_eq!(status, CALL_OK);
        assert_eq!(
            outcome,
            json!({"actions": [{"action": "mint", "generation": 1}], "next_deadline": 30_000, "live": false,
                "stopped": false, "notes": []})
        );
        let (_, outcome) = call(
            handle,
            &json!({"now": 10, "event": {"type": "mint_succeeded", "generation": 1, "ticket": "abc",
                "expires_in": 30, "max_connection_seconds": 300, "protocol": "silo.events.v2"}}),
        );
        assert_eq!(
            outcome["actions"],
            json!([{"action": "connect", "generation": 1, "path": "api/v2/events/ws?channels=user_state",
                "protocols": ["silo.events.v2", "silo.ticket.abc"]}])
        );
        let (_, outcome) = call(
            handle,
            &json!({"now": 20, "event": {"type": "opened", "generation": 1, "protocol": "silo.events.v2"}}),
        );
        assert_eq!(outcome["actions"], json!([]));
        let (_, outcome) = call(
            handle,
            &json!({"now": 30, "event": {"type": "frame", "generation": 1,
                "text": "{\"type\":\"hello\",\"required_action\":\"subscribe\"}"}}),
        );
        assert_eq!(outcome["actions"][0]["action"], "send");
        assert_eq!(outcome["actions"][0]["text"], crate::home_events::SUBSCRIBE);
        let (_, outcome) = call(
            handle,
            &json!({"now": 40, "event": {"type": "frame", "generation": 1,
                "text": "{\"type\":\"subscribed\",\"channels\":[\"user_state\"]}"}}),
        );
        assert_eq!(outcome["live"], true);
        assert_eq!(outcome["notes"], json!(["subscribed"]));
        assert_eq!(outcome["next_deadline"], 2_040);
        let (_, outcome) = call(handle, &json!({"now": 2_040, "event": {"type": "tick"}}));
        assert_eq!(outcome["actions"], json!([{"action": "refresh"}]));
        let (_, outcome) = call(
            handle,
            &json!({"now": 60_000, "event": {"type": "closed", "generation": 1, "code": 4002}}),
        );
        assert_eq!(outcome["stopped"], true);
        assert_eq!(
            outcome["notes"],
            json!([
                "closed (4002 credential_invalid)",
                "stopped: credential invalid"
            ])
        );
        let (_, outcome) = call(
            handle,
            &json!({"now": 61_000, "event": {"type": "mint_failed", "generation": 1, "status": null,
                "retry_after_ms": null}}),
        );
        assert_eq!(outcome["actions"], json!([]));
        unsafe { bale_core_home_events_destroy(handle) };
    }

    #[test]
    fn a_policy_and_the_other_events_cross_too() {
        let handle = bale_core_home_events_create();
        let policy = json!({"backoff_base_ms": 10, "backoff_max_ms": 40, "retry_after_max_ms": 100, "poll_ms": 1_000,
            "debounce_ms": 5, "mint_timeout_ms": 300, "connect_timeout_ms": 200, "subscribe_timeout_ms": 150,
            "stable_ms": 50, "lifetime_grace_ms": 20});
        let (_, outcome) = call(
            handle,
            &json!({"now": 0, "event": {"type": "start", "profile_id": "p1", "policy": policy}}),
        );
        assert_eq!(outcome["next_deadline"], 300);
        let (_, outcome) = call(
            handle,
            &json!({"now": 1, "event": {"type": "mint_failed", "generation": 1, "status": 503,
                "retry_after_ms": 1_000}}),
        );
        // Retry-After within the policy's cap.
        assert_eq!(outcome["next_deadline"], 101);
        let (_, outcome) = call(handle, &json!({"now": 101, "event": {"type": "tick"}}));
        assert_eq!(
            outcome["actions"],
            json!([{"action": "mint", "generation": 2}])
        );
        call(
            handle,
            &json!({"now": 102, "event": {"type": "mint_succeeded", "generation": 2, "ticket": "t",
                "expires_in": 30, "max_connection_seconds": 300, "protocol": "silo.events.v2"}}),
        );
        let (_, outcome) = call(
            handle,
            &json!({"now": 103, "event": {"type": "connect_failed", "generation": 2, "status": 401}}),
        );
        assert_eq!(
            outcome["notes"],
            json!(["socket refused (401)", "retrying in 20 ms"])
        );
        unsafe { bale_core_home_events_destroy(handle) };
    }

    #[test]
    fn bad_input_fails_without_quoting_it() {
        let handle = bale_core_home_events_create();
        for input in [
            json!({"now": 0, "event": {"type": "frame", "generation": 1}}),
            json!({"now": 0, "event": {"type": "secret-ticket-value"}}),
            json!({"event": {"type": "tick"}}),
        ] {
            let (status, message) = call(handle, &input);
            assert_eq!(status, CALL_FAILED);
            let message = message.as_str().unwrap();
            assert!(message.starts_with("home events: event is not valid JSON"));
            assert!(!message.contains("secret"));
        }
        unsafe {
            bale_core_home_events_destroy(handle);
            let mut output = std::ptr::null_mut();
            let mut output_len = 0;
            assert_eq!(
                bale_core_home_events_call(
                    std::ptr::null_mut(),
                    std::ptr::null(),
                    0,
                    &mut output,
                    &mut output_len
                ),
                CALL_INVALID_ARGUMENT
            );
        }
    }
}
