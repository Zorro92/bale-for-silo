//! The controller on its own: the server's conformance vectors replayed case for case (the same file
//! `BufferingQualityVectorTests.cs` reads), plus the signal mapper's cases.

use serde_json::Value;

use super::*;

const VECTORS: &str =
    include_str!("../../../../tests/Bale.Core.Tests/Fixtures/buffering-quality-v1-vectors.json");

/// The host's side, as `BufferingQualityController.cs` and `PolledBufferingQualityClock` do it: each
/// scheduled timer waits on a manual clock; due ones fire in deadline order, first scheduled first.
struct Harness {
    controller: Controller,
    mode: Mode,
    sends_fields: bool,
    measured_kbps: Option<i32>,
    now: i64,
    order: u64,
    /// (due, order, timer, cancelled)
    timers: Vec<(i64, u64, Timer, bool)>,
    /// The last timer scheduled per kind (the wrapper's slot), as an index into `timers`.
    slots: [Option<usize>; 4],
    outputs: Vec<(i64, Step)>,
}

impl Harness {
    fn new(mode: Mode, sends_fields: bool) -> Self {
        Self {
            controller: Controller::new(),
            mode,
            sends_fields,
            measured_kbps: None,
            now: 0,
            order: 0,
            timers: Vec::new(),
            slots: [None; 4],
            outputs: Vec::new(),
        }
    }

    fn call<R>(&mut self, f: impl FnOnce(&mut Controller) -> R) -> R {
        self.controller.set_inputs(Inputs {
            now: self.now,
            mode: self.mode,
            sends_fields: self.sends_fields,
            measured_kbps: self.measured_kbps,
        });
        let result = f(&mut self.controller);
        let steps = self.controller.steps().to_vec();
        self.controller.clear_steps();
        for step in steps {
            match step {
                Step::Schedule { timer, at } => {
                    let due = self.now + (at - self.now).max(0);
                    self.timers.push((due, self.order, timer, false));
                    self.order += 1;
                    self.slots[timer.index()] = Some(self.timers.len() - 1);
                }
                Step::Cancel { timer } => {
                    if let Some(index) = self.slots[timer.index()].take() {
                        self.timers[index].3 = true;
                    }
                }
                output => self.outputs.push((self.now, output)),
            }
        }
        result
    }

    fn next_due(&self) -> Option<i64> {
        self.timers
            .iter()
            .filter(|timer| !timer.3)
            .map(|timer| timer.0)
            .min()
    }

    fn fire(&mut self) {
        loop {
            let next = self
                .timers
                .iter()
                .enumerate()
                .filter(|(_, timer)| !timer.3 && timer.0 <= self.now)
                .min_by_key(|(_, timer)| (timer.0, timer.1))
                .map(|(index, timer)| (index, timer.2));
            let Some((index, timer)) = next else {
                break;
            };
            self.timers[index].3 = true;
            self.slots[timer.index()] = None;
            self.call(|c| c.timer_fired(timer));
        }
    }

    fn advance_to(&mut self, ms: i64) {
        assert!(ms >= self.now, "the clock only moves forward");
        while let Some(due) = self.next_due()
            && due <= ms
        {
            self.now = self.now.max(due);
            self.fire();
        }
        self.now = ms;
        self.fire();
    }

    fn pending(&self) -> usize {
        self.timers.iter().filter(|timer| !timer.3).count()
    }

    fn summaries(&self) -> Vec<&Summary> {
        self.outputs
            .iter()
            .filter_map(|(_, step)| match step {
                Step::Summary { summary } => Some(summary),
                _ => None,
            })
            .collect()
    }

    fn replans(&self) -> Vec<&Replan> {
        self.outputs
            .iter()
            .filter_map(|(_, step)| match step {
                Step::Replan { replan } => Some(replan),
                _ => None,
            })
            .collect()
    }
}

fn vectors() -> Value {
    serde_json::from_str(VECTORS).expect("the vectors parse")
}

fn plan(value: &Value) -> Plan {
    let warnings = value["degradation_warnings"].as_array();
    // The vectors write a missing delivered bitrate as 0.
    let delivered = value["delivered_kbps"].as_i64().map(|kbps| kbps as i32);
    Plan {
        id: value["plan_id"].as_str().unwrap().to_owned(),
        delivered_kbps: delivered.filter(|&kbps| kbps != 0),
        quality: value["quality"].as_str().unwrap().to_owned(),
        available_qualities: value["available_qualities"]
            .as_array()
            .unwrap()
            .iter()
            .map(|rung| Rung {
                label: rung["label"].as_str().unwrap().to_owned(),
                bitrate_kbps: rung["bitrate_kbps"].as_i64().unwrap() as i32,
            })
            .collect(),
        quality_reduction_unavailable: warnings.is_some_and(|warnings| {
            warnings
                .iter()
                .any(|warning| warning == "quality_reduction_unavailable")
        }),
        active_quality: value["active_quality"].as_str().map(str::to_owned),
        requested_bandwidth_estimate_kbps: None,
    }
}

fn mode(name: &str) -> Mode {
    match name {
        "never" => Mode::Never,
        "ask" => Mode::Ask,
        "always" => Mode::Always,
        other => panic!("unknown mode {other}"),
    }
}

/// A replan body as the vectors write it: every present field, keys sorted, values as JSON text.
fn canonical(body: &serde_json::Map<String, Value>, keep_buffering_fields: bool) -> String {
    let mut pairs: Vec<(String, String)> = body
        .iter()
        .filter(|(key, _)| {
            keep_buffering_fields
                || !matches!(key.as_str(), "bandwidth_estimate_source" | "learned_limit")
        })
        .map(|(key, value)| (key.clone(), value.to_string()))
        .collect();
    pairs.sort();
    format!("{pairs:?}")
}

fn replan_body(replan: &Replan) -> serde_json::Map<String, Value> {
    let mut body = serde_json::Map::new();
    body.insert("operation".into(), replan.operation.clone().into());
    body.insert(
        "quality_preference".into(),
        replan.quality_preference.clone().into(),
    );
    if let Some(estimate) = replan.bandwidth_estimate_kbps {
        body.insert("bandwidth_estimate_kbps".into(), estimate.into());
    }
    if let Some(source) = replan.bandwidth_estimate_source {
        body.insert("bandwidth_estimate_source".into(), source.into());
    }
    if let Some(limit) = replan.learned_limit {
        body.insert("learned_limit".into(), limit.into());
    }
    body
}

fn wire_action(action: Action) -> String {
    serde_json::to_value(action)
        .unwrap()
        .as_str()
        .unwrap()
        .to_owned()
}

/// Feeds one event; returns the adopted plan's id for plan_adopted.
fn apply(h: &mut Harness, item: &Value) -> Option<String> {
    let kind = item["type"].as_str().unwrap();
    match kind {
        "plan_adopted" => {
            let plan = plan(&item["plan"]);
            let id = plan.id.clone();
            let initiated = item["controllerInitiated"].as_bool().unwrap_or(true);
            h.call(|c| c.plan_adopted(plan, initiated));
            return Some(id);
        }
        "playing" => h.call(Controller::playing),
        "waiting" => {
            let cause = match item["cause"].as_str() {
                None => WaitCause::Network,
                Some("seek") => WaitCause::Seek,
                Some("pause") => WaitCause::Pause,
                Some("reload") => WaitCause::Reload,
                Some("renderer") => WaitCause::Renderer,
                Some(other) => panic!("unknown wait cause {other}"),
            };
            h.call(|c| c.waiting(cause, 0));
        }
        "pause" => h.call(Controller::paused),
        "seek" => h.call(Controller::seeked),
        "transport_error_start" => h.call(|c| c.transport_trouble_changed(true)),
        "transport_error_end" => h.call(|c| c.transport_trouble_changed(false)),
        "replan_failed" => h.call(Controller::replan_failed),
        "select_lower" => h.call(Controller::select_lower),
        "select_keep" => h.call(Controller::select_keep),
        "select_restore" => h.call(Controller::select_restore),
        "back" => {
            h.call(Controller::back);
        }
        "pill_obscured" => h.call(|c| c.set_competing_pill_visible(true)),
        "pill_unobscured" => h.call(|c| c.set_competing_pill_visible(false)),
        "client_replan" => {
            let operation = item["operation"].as_str().unwrap().to_owned();
            assert!(
                [
                    "track_change",
                    "seek_reanchor",
                    "seek_failure_recovery",
                    "failure_recovery",
                    "output_change"
                ]
                .contains(&operation.as_str())
            );
            // What the player sends itself with the controller's fields.
            if let Some(replan) = h.call(|c| c.client_replan(operation)) {
                h.outputs.push((h.now, Step::Replan { replan }));
            }
        }
        "quality_changed" => h.call(Controller::quality_changed),
        "pick_sent" => h.call(Controller::pick_sent),
        "pick_refused" => h.call(Controller::pick_refused),
        "new_attempt" => h.call(Controller::new_attempt),
        other => panic!("unknown event type {other}"),
    }
    None
}

fn replay(vectors: &Value, case: &Value, sends_fields: bool) {
    let name = case["name"].as_str().unwrap();
    let mode = mode(
        case["mode"]
            .as_str()
            .unwrap_or_else(|| vectors["defaults"]["mode"].as_str().unwrap()),
    );
    let mut h = Harness::new(mode, sends_fields);
    // The vectors' waits are a link too slow for every plan: each plan's media measures below its bitrate.
    h.measured_kbps = Some(1);
    let mut adopted = Vec::new();
    for item in case["events"].as_array().unwrap() {
        h.advance_to(item["t"].as_i64().unwrap());
        if let Some(id) = apply(&mut h, item) {
            adopted.push(id);
        }
    }
    h.advance_to(case["until"].as_i64().unwrap());
    h.call(Controller::plan_ended);

    let expected: Vec<String> = case["expect"]
        .as_array()
        .unwrap()
        .iter()
        .map(|output| {
            let t = output["t"].as_i64().unwrap();
            match output["type"].as_str().unwrap() {
                "replan" => format!(
                    "{t} replan {}",
                    canonical(output["body"].as_object().unwrap(), sends_fields)
                ),
                "notice" => format!("{t} notice {}", output["key"].as_str().unwrap()),
                kind @ ("prompt_shown" | "undo_shown") => format!("{t} {kind}"),
                other => panic!("unknown output type {other}"),
            }
        })
        .collect();
    let actual: Vec<String> = h
        .outputs
        .iter()
        .filter_map(|(t, step)| match step {
            Step::Replan { replan } => Some(format!(
                "{t} replan {}",
                canonical(&replan_body(replan), true)
            )),
            Step::PillShown { pill } => Some(match pill {
                PillKind::Prompt => format!("{t} prompt_shown"),
                PillKind::Undo => format!("{t} undo_shown"),
            }),
            Step::Notice { notice } => Some(match notice {
                Notice::Lowered => format!("{t} notice lowered"),
                Notice::CantLower => format!("{t} notice cant_lower"),
            }),
            _ => None,
        })
        .collect();
    assert_eq!(expected, actual, "{name} (sends_fields={sends_fields})");
    let summary_ids: Vec<&str> = h.summaries().iter().map(|s| s.plan_id.as_str()).collect();
    assert_eq!(adopted, summary_ids, "{name}: one summary per adopted plan");
    if let Some(actions) = case["buffering_action"].as_object() {
        for (plan_id, action) in actions {
            let summary = h
                .summaries()
                .into_iter()
                .find(|summary| &summary.plan_id == plan_id)
                .unwrap_or_else(|| panic!("{name}: no summary for {plan_id}"));
            assert_eq!(
                action.as_str().unwrap(),
                wire_action(summary.action),
                "{name}: {plan_id}"
            );
        }
    }
}

#[test]
fn every_vector_case_with_and_without_the_feature() {
    let vectors = vectors();
    let cases = vectors["cases"].as_array().unwrap();
    assert_eq!(cases.len(), 66);
    for case in cases {
        replay(&vectors, case, true);
        replay(&vectors, case, false);
    }
}

#[test]
fn constants_and_vocabulary_match_the_vectors() {
    let vectors = vectors();
    let constants = &vectors["constants"];
    assert_eq!(constants["STARTUP_GRACE_MS"], STARTUP_GRACE_MS);
    assert_eq!(constants["STALL_MIN_MS"], STALL_MIN_MS);
    assert_eq!(
        constants["STALLS_BEFORE_TRIGGER"],
        STALLS_BEFORE_TRIGGER as i64
    );
    assert_eq!(constants["STALL_WINDOW_MS"], STALL_WINDOW_MS);
    assert_eq!(constants["LONG_STALL_MS"], LONG_STALL_MS);
    assert_eq!(constants["PROMPT_SECONDS"], PROMPT_SECONDS);
    assert_eq!(constants["REOFFER_MIN_MS"], REOFFER_MIN_MS);
    assert_eq!(constants["NO_LOWER_COOLDOWN_MS"], NO_LOWER_COOLDOWN_MS);
    assert_eq!(
        constants["REARM_AFTER_UNADOPTED_MS"],
        REARM_AFTER_UNADOPTED_MS
    );
    assert_eq!(constants["STEP_DOWN_RATIO"], 0.7);
    assert_eq!(constants["MIN_ESTIMATE_KBPS"], MIN_ESTIMATE_KBPS);
    let mut events: Vec<&str> = vectors["event_types"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| e.as_str().unwrap())
        .collect();
    events.sort_unstable();
    let mut known = vec![
        "plan_adopted",
        "playing",
        "waiting",
        "pause",
        "seek",
        "transport_error_start",
        "transport_error_end",
        "replan_failed",
        "select_lower",
        "select_keep",
        "select_restore",
        "back",
        "pill_obscured",
        "pill_unobscured",
        "client_replan",
        "quality_changed",
        "pick_sent",
        "pick_refused",
        "new_attempt",
    ];
    known.sort_unstable();
    assert_eq!(known, events);
}

fn ladder_plan(id: &str, delivered: Option<i32>, quality: &str) -> Plan {
    let rung = |label: &str, bitrate_kbps| Rung {
        label: label.to_owned(),
        bitrate_kbps,
    };
    Plan {
        id: id.to_owned(),
        delivered_kbps: delivered,
        quality: quality.to_owned(),
        available_qualities: vec![
            rung("original", 63_000),
            rung("1080p-high", 10_000),
            rung("1080p-medium", 6_000),
            rung("720p-high", 4_000),
        ],
        quality_reduction_unavailable: false,
        active_quality: None,
        requested_bandwidth_estimate_kbps: None,
    }
}

#[test]
fn a_long_stall_lowers_and_the_timers_it_no_longer_needs_go() {
    let mut h = Harness::new(Mode::Always, true);
    h.measured_kbps = Some(1);
    h.call(|c| c.plan_adopted(ladder_plan("p1", Some(63_000), "auto"), true));
    h.call(Controller::playing);
    h.advance_to(125_000);
    h.call(|c| c.waiting(WaitCause::Network, 0));
    h.advance_to(130_999);
    assert!(h.replans().is_empty());
    h.advance_to(131_000);
    assert_eq!(h.replans().len(), 1);
    assert_eq!(h.replans()[0].bandwidth_estimate_kbps, Some(44_100));
    assert_eq!(h.controller.kept_estimate_kbps(), Some(44_100));
    // Only the rearm waits for the lowering's adoption.
    assert_eq!(h.pending(), 1);
    h.advance_to(134_000);
    h.call(|c| c.plan_adopted(ladder_plan("p2", Some(31_600), "auto"), true));
    h.call(Controller::playing);
    assert_eq!(h.controller.pill().map(|p| p.kind), Some(PillKind::Undo));
    assert_eq!(h.controller.pill().unwrap().deadline_ms, Some(149_000));
    // The undo pill's timer only.
    assert_eq!(h.pending(), 1);
    h.advance_to(149_000);
    assert_eq!(h.controller.pill(), None);
    assert_eq!(h.pending(), 0);
}

#[test]
fn step_down_and_lower_rung() {
    assert_eq!(step_down_estimate(Some(63_000)), Some(44_100));
    assert_eq!(step_down_estimate(Some(22_120)), Some(15_484));
    assert_eq!(step_down_estimate(Some(143)), Some(100));
    assert_eq!(step_down_estimate(Some(142)), None);
    assert_eq!(step_down_estimate(Some(0)), None);
    assert_eq!(step_down_estimate(None), None);
    assert_eq!(step_down_estimate(Some(i32::MAX)), Some(1_503_238_552));

    let mut named = ladder_plan("p1", Some(8_000), "1080p-high");
    named.active_quality = Some("1080p-high".into());
    assert_eq!(
        lower_rung(&named).map(|r| r.label.as_str()),
        Some("1080p-medium")
    );
    let auto = ladder_plan("p2", Some(63_000), "auto");
    assert_eq!(
        lower_rung(&auto).map(|r| r.label.as_str()),
        Some("1080p-high")
    );
    assert!(lower_rung(&ladder_plan("p3", None, "auto")).is_none());
}

// ---- Signals (BufferingSignalsTests.cs) ----

const PLAYING: Snapshot = Snapshot {
    has_mount: true,
    wants_to_play: true,
    playing: true,
    seek_in_flight: false,
    reached_end: false,
    upstream_silent: false,
};
const BUFFERING: Snapshot = Snapshot {
    playing: false,
    ..PLAYING
};
const PAUSED: Snapshot = Snapshot {
    wants_to_play: false,
    playing: false,
    ..PLAYING
};

fn calls(signals: &mut Signals, snapshot: Snapshot) -> Vec<Signal> {
    let (calls, count) = signals.update(snapshot);
    calls[..count].iter().map(|call| call.unwrap()).collect()
}

#[test]
fn a_start_waits_then_plays_and_repeats_report_nothing() {
    let mut s = Signals::new();
    s.plan_adopted();
    let start = Snapshot {
        wants_to_play: true,
        ..Snapshot::default()
    };
    assert_eq!(calls(&mut s, start), [Signal::WaitingNetwork]);
    assert_eq!(calls(&mut s, BUFFERING), []);
    assert_eq!(calls(&mut s, PLAYING), [Signal::Playing]);
    assert_eq!(calls(&mut s, PLAYING), []);
    assert!(s.plan_has_run());
    assert_eq!(calls(&mut s, BUFFERING), [Signal::WaitingNetwork]);
    assert_eq!(calls(&mut s, PAUSED), [Signal::Paused]);
}

#[test]
fn buffering_after_a_seek_stays_the_seeks_until_playback_runs() {
    let mut s = Signals::new();
    s.plan_adopted();
    calls(&mut s, PLAYING);
    let seeking = Snapshot {
        seek_in_flight: true,
        ..BUFFERING
    };
    assert_eq!(calls(&mut s, seeking), [Signal::WaitingSeek]);
    assert_eq!(calls(&mut s, BUFFERING), []);
    assert_eq!(calls(&mut s, PLAYING), [Signal::Playing]);
    assert_eq!(calls(&mut s, BUFFERING), [Signal::WaitingNetwork]);
}

#[test]
fn silence_while_waiting_is_an_outage_until_data_arrives_or_playback_runs() {
    let mut s = Signals::new();
    s.plan_adopted();
    calls(&mut s, PLAYING);
    assert_eq!(calls(&mut s, BUFFERING), [Signal::WaitingNetwork]);
    let silent = Snapshot {
        upstream_silent: true,
        ..BUFFERING
    };
    assert_eq!(calls(&mut s, silent), [Signal::TroubleStarted]);
    assert_eq!(calls(&mut s, BUFFERING), []);
    s.note_media_data_arrived();
    assert_eq!(calls(&mut s, BUFFERING), [Signal::TroubleEnded]);
    s.note_media_request_failed();
    assert_eq!(calls(&mut s, BUFFERING), [Signal::TroubleStarted]);
    assert_eq!(
        calls(&mut s, PLAYING),
        [Signal::TroubleEnded, Signal::Playing]
    );
}

#[test]
fn a_replaced_source_is_a_reload_once_the_plan_has_run() {
    let mut s = Signals::new();
    s.plan_adopted();
    calls(&mut s, PLAYING);
    let unmounted = Snapshot {
        wants_to_play: true,
        ..Snapshot::default()
    };
    assert_eq!(calls(&mut s, unmounted), [Signal::WaitingReload]);
    assert_eq!(calls(&mut s, Snapshot::default()), [Signal::Paused]);
}

#[test]
fn snapshot_bits() {
    assert_eq!(Snapshot::from_bits(1 | 2 | 4), PLAYING);
    assert_eq!(
        Snapshot::from_bits(63),
        Snapshot {
            has_mount: true,
            wants_to_play: true,
            playing: true,
            seek_in_flight: true,
            reached_end: true,
            upstream_silent: true,
        }
    );
}
