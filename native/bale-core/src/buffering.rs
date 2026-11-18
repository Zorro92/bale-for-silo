//! Lowering quality when playback keeps buffering: `BufferingQualityController` and `BufferingSignals`
//! from `src/Bale.Core/Playback` (docs/rust-port.md, "Buffering quality").
//!
//! The controller is silo-server docs/design/2026-10-17-buffering-quality.md, its stall tracker and
//! trigger (Terms) and every table under "Behaviour", ported from silo-apple's
//! BufferingQualityController.swift (playback follows Apple; Android's BufferingQualityController.kt is
//! the same spec). The tests replay the server's conformance vectors
//! (schemas/buffering-quality/v1/vectors.json) case for case.
//!
//! It performs no I/O and owns no clock. Each call carries the clock's time, the settings and the
//! measured throughput ([`Inputs`]), and leaves an ordered list of [`Step`]s: the timers to schedule or
//! cancel on the host's clock and the outputs to raise (replans, pills, notices, plan summaries). The
//! host applies them in order, so a timer the host fires comes back as [`Controller::timer_fired`].

use serde::{Deserialize, Serialize};

// Spec constants (vectors.json "constants").
pub const STARTUP_GRACE_MS: i64 = 10_000;
pub const STALL_MIN_MS: i64 = 1_000;
pub const STALLS_BEFORE_TRIGGER: usize = 2;
pub const STALL_WINDOW_MS: i64 = 120_000;
pub const LONG_STALL_MS: i64 = 6_000;
pub const PROMPT_SECONDS: i64 = 15;
pub const REOFFER_MIN_MS: i64 = 120_000;
pub const NO_LOWER_COOLDOWN_MS: i64 = 300_000;
pub const REARM_AFTER_UNADOPTED_MS: i64 = 60_000;
pub const MIN_ESTIMATE_KBPS: i32 = 100;
pub const PROMPT_MS: i64 = PROMPT_SECONDS * 1000;

pub const AUTO_QUALITY: &str = "auto";
pub const QUALITY_CHANGE_OPERATION: &str = "quality_change";
pub const OBSERVED_REBUFFER_SOURCE: &str = "observed_rebuffer";
/// `PlaybackProtocol.LearnedLimitClear`.
pub const LEARNED_LIMIT_CLEAR: &str = "clear";

/// One `available_qualities` entry.
#[derive(Debug, Clone, Deserialize)]
pub struct Rung {
    pub label: String,
    pub bitrate_kbps: i32,
}

/// What the controller reads from an adopted playback plan (C# `BufferingQualityPlan`).
#[derive(Debug, Clone, Deserialize)]
pub struct Plan {
    /// The adoption's identity: every adoption is a new plan, so the host gives each one a new id. The
    /// server's `plan_id` is a hash of the route and can repeat, so keying on it would hide a lowering
    /// that came back unchanged.
    pub id: String,
    /// `effective_recipe.bitrate_kbps`, else `source.bitrate_kbps`.
    pub delivered_kbps: Option<i32>,
    /// `auto`, or the label of the rung the viewer picked.
    pub quality: String,
    pub available_qualities: Vec<Rung>,
    /// `degradation_warnings` holds `quality_reduction_unavailable`.
    pub quality_reduction_unavailable: bool,
    /// The rung `active_quality` names (with plan_active_quality_v1), or none.
    pub active_quality: Option<String>,
    /// The estimate the plan was requested with, for `plan_summary`.
    pub requested_bandwidth_estimate_kbps: Option<i32>,
}

impl Plan {
    pub fn is_auto(&self) -> bool {
        self.quality == AUTO_QUALITY
    }
}

/// `BufferingQualityMode`, by its wire name.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    Never,
    Ask,
    #[default]
    Always,
}

/// Why the player waits. Only a network wait can be a stall.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WaitCause {
    #[default]
    Network,
    Seek,
    Pause,
    Reload,
    /// The wait began with more than RENDERER_WAIT_BUFFER_MS buffered: the decoder is behind, not the network.
    Renderer,
}

/// The fields of a replan for the host to send (C# `BufferingQualityReplan`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Replan {
    pub operation: String,
    pub quality_preference: String,
    pub bandwidth_estimate_kbps: Option<i32>,
    /// `observed_rebuffer`; only to a server that advertises buffering_quality_v1.
    pub bandwidth_estimate_source: Option<&'static str>,
    /// `clear`; only to a server that advertises buffering_quality_v1.
    pub learned_limit: Option<&'static str>,
    /// The evidence a lowering before anything of the attempt played rested on. None on every other
    /// replan: the player adds the playing plan's own figure.
    pub observed_throughput_kbps: Option<i32>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Notice {
    /// An accepted lowering came back lower.
    Lowered,
    /// No lower rung (once per title), or a lowering that came back no lower.
    CantLower,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum PillKind {
    /// "Playback keeps buffering": Lower quality / Keep quality.
    Prompt,
    /// "Quality lowered to stop buffering": Restore quality.
    Undo,
}

/// The actionable pill on screen.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct Pill {
    pub kind: PillKind,
    /// When the timer runs out on the controller's clock; none while frozen.
    pub deadline_ms: Option<i64>,
    /// Left as of the last arm or freeze.
    pub remaining_ms: i64,
}

/// `buffering_action`: the last action on a plan (wire spellings).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Action {
    None,
    Ignored,
    Lowered,
    Restored,
    Accepted,
    Kept,
    TimedOut,
    CantLower,
}

/// The `plan_summary` route event's figures, for a plan that stopped being the playing plan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Summary {
    pub plan_id: String,
    pub played_ms: i64,
    pub stall_count: i32,
    pub stall_total_ms: i64,
    pub stall_longest_ms: i64,
    pub delivered_kbps: Option<i32>,
    pub mode: Mode,
    pub action: Action,
    pub bandwidth_estimate_kbps: Option<i32>,
}

/// The controller's one-shot timers. Each maps to one timer field of the C# original.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Timer {
    LongStall,
    Suppression,
    Rearm,
    Pill,
}

impl Timer {
    const fn index(self) -> usize {
        self as usize
    }
}

/// What a call leaves for the host, in the order the C# original did it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "step", rename_all = "snake_case")]
pub enum Step {
    /// Schedule `timer` to fire at `at` on the host's clock (now if `at` has passed). A timer
    /// scheduled while the last one of its kind is still waiting leaves that one waiting too, as
    /// the original's overwritten timer field did.
    Schedule {
        timer: Timer,
        at: i64,
    },
    /// Cancel the last `timer` scheduled.
    Cancel {
        timer: Timer,
    },
    Replan {
        replan: Replan,
    },
    /// A pill came on screen: first, or again after something that hid it went away.
    PillShown {
        pill: PillKind,
    },
    Notice {
        notice: Notice,
    },
    Summary {
        summary: Summary,
    },
}

/// What each call is made with: the host clock's time, the settings, and what the playing plan's
/// media has measurably arrived at so far.
#[derive(Debug, Clone, Copy, Default, Deserialize)]
pub struct Inputs {
    pub now: i64,
    pub mode: Mode,
    /// The server advertises buffering_quality_v1; without it replans drop the source and learned_limit.
    pub sends_fields: bool,
    pub measured_kbps: Option<i32>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AttemptStage {
    New,
    Adopted,
    Played,
}

#[derive(Debug, Clone, Copy, Default)]
struct Stall {
    /// Where the trigger times this stall from; none during transport trouble. Ahead of now in the grace.
    timed_from: Option<i64>,
    /// The summary's timing, which a history clear does not re-time.
    measured_from: Option<i64>,
}

impl Stall {
    fn measured(&self, now: i64) -> i64 {
        self.measured_from.map_or(0, |from| (now - from).max(0))
    }
}

#[derive(Debug, Clone, Copy)]
struct EndedStall {
    at: i64,
    long: bool,
}

#[derive(Debug)]
struct PlanState {
    plan: Plan,
    action: Action,
    /// STARTUP_GRACE_MS after the plan's first wait or, when none came before it, its first frame.
    grace_ends_at: Option<i64>,
    stall: Option<Stall>,
    ended_stalls: Vec<EndedStall>,
    cooldown_until: Option<i64>,
    /// Replaced a plan after the attempt had played and has not played itself: a wait is a reload, not a stall.
    reloading: bool,
    played_ms: i64,
    stall_count: i32,
    stall_ended_by_reload: bool,
    stall_total_ms: i64,
    stall_longest_ms: i64,
}

impl PlanState {
    fn new(plan: Plan) -> Self {
        Self {
            plan,
            action: Action::None,
            grace_ends_at: None,
            stall: None,
            ended_stalls: Vec::new(),
            cooldown_until: None,
            reloading: false,
            played_ms: 0,
            stall_count: 0,
            stall_ended_by_reload: false,
            stall_total_ms: 0,
            stall_longest_ms: 0,
        }
    }

    fn record_stall(&mut self, measured_ms: i64) {
        if measured_ms <= 0 {
            return;
        }
        self.stall_total_ms += measured_ms;
        self.stall_longest_ms = self.stall_longest_ms.max(measured_ms);
    }
}

#[derive(Debug, Clone, Copy)]
struct PendingReplan {
    /// `always` on Auto, whose lower result shows the undo pill; otherwise Lower quality, whose result is a notice.
    automatic: bool,
    /// The delivered bitrate the replan left; a lower result is below it.
    from_kbps: Option<i32>,
    prior_kept_estimate_kbps: Option<i32>,
}

#[derive(Debug, Clone, Copy)]
struct PresentedPill {
    kind: PillKind,
    remaining_ms: i64,
    deadline_ms: Option<i64>,
    first_shown_at: Option<i64>,
    is_on_screen: bool,
}

/// The controller's state between calls (C# `BufferingQualityController`).
#[derive(Debug)]
pub struct Controller {
    inputs: Inputs,
    steps: Vec<Step>,
    /// The actionable pill on screen, or none.
    pill: Option<Pill>,
    /// The step-down estimate of the latest Auto lowering, from its replan until Restore quality, a
    /// quality change the controller did not send, or a new attempt. Later Auto replans repeat it
    /// without a source.
    kept_estimate_kbps: Option<i32>,
    current: Option<PlanState>,
    playing_since: Option<i64>,
    is_paused: bool,
    renderer_waiting: bool,
    transport_trouble: bool,
    transport_trouble_ended_at: Option<i64>,
    competing_pill_visible: bool,
    attempt_stage: AttemptStage,
    answered: bool,
    no_lower_rung_notice_shown: bool,
    last_offer_at: Option<i64>,
    pending_replan: Option<PendingReplan>,
    pick_pending: bool,
    presented: Option<PresentedPill>,
    /// Which timers are waiting on the host's clock (the original's non-null timer fields).
    armed: [bool; 4],
}

impl Default for Controller {
    fn default() -> Self {
        Self::new()
    }
}

impl Controller {
    pub fn new() -> Self {
        Self {
            inputs: Inputs::default(),
            steps: Vec::new(),
            pill: None,
            kept_estimate_kbps: None,
            current: None,
            playing_since: None,
            is_paused: false,
            renderer_waiting: false,
            transport_trouble: false,
            transport_trouble_ended_at: None,
            competing_pill_visible: false,
            attempt_stage: AttemptStage::New,
            answered: false,
            no_lower_rung_notice_shown: false,
            last_offer_at: None,
            pending_replan: None,
            pick_pending: false,
            presented: None,
            armed: [false; 4],
        }
    }

    /// Sets what the next calls run with.
    pub fn set_inputs(&mut self, inputs: Inputs) {
        self.inputs = inputs;
    }

    /// The steps the calls since the last [`Self::clear_steps`] left, in order.
    pub fn steps(&self) -> &[Step] {
        &self.steps
    }

    /// Forgets the steps, keeping the buffer for the next call.
    pub fn clear_steps(&mut self) {
        self.steps.clear();
    }

    pub fn pill(&self) -> Option<Pill> {
        self.pill
    }

    pub fn kept_estimate_kbps(&self) -> Option<i32> {
        self.kept_estimate_kbps
    }

    /// A pill is waiting, visible or hidden behind another surface.
    pub fn pill_pending(&self) -> bool {
        self.presented.is_some()
    }

    /// The adoption id of the playing plan, or none.
    pub fn current_plan_id(&self) -> Option<&str> {
        self.current.as_ref().map(|state| state.plan.id.as_str())
    }

    fn now(&self) -> i64 {
        self.inputs.now
    }

    // ---- Plans ----

    /// A plan became the playing plan. It ends the outgoing plan (emitting its summary), takes down
    /// any pill and starts fresh history. A plan that answers the controller's pending replan settles
    /// it. Pass `controller_initiated` false for a plan answering a replan the controller did not send.
    pub fn plan_adopted(&mut self, plan: Plan, controller_initiated: bool) {
        let now = self.now();
        // During the start, a stall in progress carries over as the new plan's first wait.
        let was_stalled = self
            .current
            .as_ref()
            .is_some_and(|state| state.stall.is_some());
        let start_grace = if self.attempt_stage == AttemptStage::Adopted {
            self.current.as_ref().and_then(|state| state.grace_ends_at)
        } else {
            None
        };
        self.end_plan(now);
        let pending = self.pending_replan.take().filter(|_| controller_initiated);
        let mut state = PlanState::new(plan);
        state.grace_ends_at = start_grace;
        // A plan adopted while playing has its first frame now, and its grace counts from there.
        if self.playing_since.is_some() {
            self.playing_since = Some(now);
            state.grace_ends_at = Some(now + STARTUP_GRACE_MS);
        } else {
            state.reloading = self.attempt_stage == AttemptStage::Played;
        }
        if self.attempt_stage == AttemptStage::New {
            self.attempt_stage = AttemptStage::Adopted;
        }
        let reloading = state.reloading;
        self.current = Some(state);
        if was_stalled && !reloading {
            self.begin_stall(now);
        }
        if let Some(pending) = pending {
            self.settle(pending, now);
        }
    }

    /// The playing plan's summary as if it ended now, without ending it.
    pub fn running_summary(&self) -> Option<Summary> {
        self.current
            .as_ref()
            .map(|state| self.summary(state, self.now()))
    }

    /// The plan stopped or failed with no successor: emits its summary.
    pub fn plan_ended(&mut self) {
        self.end_plan(self.now());
        self.pending_replan = None;
        self.playing_since = None;
    }

    /// A new playback attempt of the same title: the kept estimate goes, its first plan starts afresh.
    pub fn new_attempt(&mut self) {
        self.attempt_stage = AttemptStage::New;
        self.kept_estimate_kbps = None;
        if let Some(pending) = &mut self.pending_replan {
            pending.prior_kept_estimate_kbps = None;
        }
    }

    // ---- Media events ----

    pub fn playing(&mut self) {
        let now = self.now();
        self.is_paused = false;
        self.renderer_waiting = false;
        if let Some(plan) = &mut self.current {
            // The plan ran again: a reload that ended its stall did not end it.
            plan.stall_ended_by_reload = false;
            plan.reloading = false;
        }
        if self.playing_since.is_none() {
            self.playing_since = Some(now);
            if let Some(fresh) = &mut self.current
                && fresh.grace_ends_at.is_none()
            {
                fresh.grace_ends_at = Some(now + STARTUP_GRACE_MS);
            }
        }
        // The start wait the first frame ends counts only on evidence.
        let counts = self.attempt_stage == AttemptStage::Played
            || self
                .current
                .as_ref()
                .is_some_and(|state| self.arrives_slowly(&state.plan));
        if self.current.is_some() {
            self.attempt_stage = AttemptStage::Played;
        }
        self.end_stall(now, counts, false);
        self.refresh_pill();
        self.evaluate(now);
    }

    /// The player waits. A network wait is a stall, except while a replacement of a plan that played
    /// loads; any other cause ends a stall in progress without counting it. `started_ago_ms`
    /// back-dates a wait noticed late, never past the start of the current playing state.
    pub fn waiting(&mut self, cause: WaitCause, started_ago_ms: i64) {
        let now = self.now();
        self.renderer_waiting = cause == WaitCause::Renderer;
        let began = match self.playing_since {
            Some(since) => since.max(now - started_ago_ms.max(0)),
            None => now,
        };
        self.leave_playing(began);
        if cause != WaitCause::Network {
            self.end_stall(now, false, cause == WaitCause::Reload);
            if cause == WaitCause::Renderer {
                self.is_paused = false;
                self.refresh_pill();
            }
            return;
        }
        self.is_paused = false;
        if self
            .current
            .as_ref()
            .is_some_and(|state| state.stall.is_none() && !state.reloading)
        {
            self.begin_stall(began);
        }
        self.refresh_pill();
    }

    pub fn paused(&mut self) {
        let now = self.now();
        self.renderer_waiting = false;
        self.leave_playing(now);
        self.end_stall(now, false, false);
        self.is_paused = true;
        self.refresh_pill();
    }

    pub fn seeked(&mut self) {
        self.end_stall(self.now(), false, false);
    }

    /// An outage: media requests failing or silent, or the device offline. A stall that runs into it
    /// drops its time so far, and stall time during it never counts; a stall still in progress when it
    /// ends is timed from then.
    pub fn transport_trouble_changed(&mut self, trouble: bool) {
        if trouble == self.transport_trouble {
            return;
        }
        self.transport_trouble = trouble;
        let now = self.now();
        if !trouble {
            self.transport_trouble_ended_at = Some(now);
        }
        let Some(state) = &mut self.current else {
            return;
        };
        let grace_ends_at = state.grace_ends_at;
        let Some(stall) = &mut state.stall else {
            return;
        };
        if trouble {
            stall.measured_from = None;
            stall.timed_from = None;
            self.cancel_stall_timers();
        } else {
            let from = now.max(grace_ends_at.unwrap_or(now));
            stall.timed_from = Some(from);
            stall.measured_from = Some(from);
            self.schedule_long_stall_timer();
        }
    }

    /// The controller's replan failed. The plan keeps playing and is watched again at the rearm.
    pub fn replan_failed(&mut self) {
        self.pending_replan = None;
    }

    /// The player could not send the controller's replan at all: it fails like
    /// [`Self::replan_failed`], and keeps no estimate.
    pub fn replan_refused(&mut self) {
        if let Some(pending) = self.pending_replan.take() {
            self.kept_estimate_kbps = pending.prior_kept_estimate_kbps;
        }
    }

    /// The plan failed before its first frame because the player's own start deadline passed. On Auto
    /// in `always` with evidence that its media arrived below its delivered bitrate, it lowers as a
    /// long stall would and returns true; false leaves the failure to the player's usual recovery.
    pub fn start_deadline_passed(&mut self) -> bool {
        let now = self.now();
        if self.inputs.mode != Mode::Always
            || self.answered
            || self.transport_trouble
            || self
                .transport_trouble_ended_at
                .is_some_and(|ended| now - ended < LONG_STALL_MS)
            || self.renderer_waiting
            || self.pick_pending
            || self.armed[Timer::Rearm.index()]
        {
            return false;
        }
        let Some(state) = &self.current else {
            return false;
        };
        let plan = &state.plan;
        if !plan.is_auto() || plan.quality_reduction_unavailable {
            return false;
        }
        let Some(delivered) = plan.delivered_kbps else {
            return false;
        };
        if !self.arrives_slowly(plan)
            || state.cooldown_until.is_some()
            || lower_rung(plan).is_none()
        {
            return false;
        }
        let Some(estimate) = step_down_estimate(Some(delivered)) else {
            return false;
        };
        let observed = self.observed_throughput_kbps(plan);
        self.withdraw_pill();
        self.send_replan(
            Action::Lowered,
            AUTO_QUALITY.to_owned(),
            Some(estimate),
            None,
            Some(delivered),
            observed,
        );
        true
    }

    // ---- Pill answers ----

    fn visible_pill_kind(&self) -> Option<PillKind> {
        self.pill.map(|pill| pill.kind)
    }

    /// Lower quality on the prompt.
    pub fn select_lower(&mut self) {
        if self.visible_pill_kind() != Some(PillKind::Prompt) {
            return;
        }
        let Some(state) = &self.current else {
            return;
        };
        let plan = &state.plan;
        let delivered = plan.delivered_kbps;
        let is_auto = plan.is_auto();
        let rung = lower_rung(plan).map(|rung| rung.label.clone());
        let from_named = delivered.or_else(|| active_bitrate(plan));
        self.withdraw_pill();
        let Some(rung) = rung else {
            self.cant_lower();
            return;
        };
        if is_auto {
            let Some(estimate) = step_down_estimate(delivered) else {
                self.cant_lower();
                return;
            };
            self.send_replan(
                Action::Accepted,
                AUTO_QUALITY.to_owned(),
                Some(estimate),
                None,
                delivered,
                None,
            );
        }
        // This playback only: the saved preference does not change.
        else {
            self.send_replan(Action::Accepted, rung, None, None, from_named, None);
        }
    }

    /// Keep quality on the prompt: answers the title at this quality.
    pub fn select_keep(&mut self) {
        if self.visible_pill_kind() != Some(PillKind::Prompt) {
            return;
        }
        self.withdraw_pill();
        self.answered = true;
        if let Some(state) = &mut self.current {
            state.action = Action::Kept;
        }
    }

    /// Restore quality on the undo pill: answers the title and clears the learned limit.
    pub fn select_restore(&mut self) {
        if self.visible_pill_kind() != Some(PillKind::Undo) || self.current.is_none() {
            return;
        }
        self.withdraw_pill();
        self.answered = true;
        self.kept_estimate_kbps = None;
        self.send_replan(
            Action::Restored,
            AUTO_QUALITY.to_owned(),
            None,
            Some(LEARNED_LIMIT_CLEAR),
            None,
            None,
        );
    }

    /// Back. On the prompt it answers like Keep; on the undo pill it only hides it. True when a
    /// visible pill consumed the press, so a second Back behaves normally.
    pub fn back(&mut self) -> bool {
        match self.visible_pill_kind() {
            Some(PillKind::Prompt) => {
                self.select_keep();
                true
            }
            Some(PillKind::Undo) => {
                self.withdraw_pill();
                self.evaluate_long_stall();
                true
            }
            None => false,
        }
    }

    /// Select at the player root: the visible pill's primary action.
    pub fn select_primary(&mut self) {
        match self.visible_pill_kind() {
            Some(PillKind::Prompt) => self.select_lower(),
            Some(PillKind::Undo) => self.select_restore(),
            None => {}
        }
    }

    // ---- Host inputs ----

    /// A quality change the controller did not send (the viewer's pick, as its plan is adopted).
    pub fn quality_changed(&mut self) {
        self.pick_pending = false;
        self.kept_estimate_kbps = None;
        self.answered = false;
        self.last_offer_at = None;
        self.pending_replan = None;
        self.cancel(Timer::Rearm);
        self.withdraw_pill();
        self.clear_history(self.now());
    }

    /// The viewer's quality pick was sent: pills hide, nothing prompts or lowers while it is pending.
    pub fn pick_sent(&mut self) {
        self.pick_pending = true;
        self.withdraw_pill();
    }

    /// The pick never became a plan: nothing else changes, and a long stall in progress is evaluated at once.
    pub fn pick_refused(&mut self) {
        if !self.pick_pending {
            return;
        }
        self.pick_pending = false;
        self.evaluate_long_stall();
    }

    /// A replan the player sends for its own reasons (track change, seek reanchor, failure recovery):
    /// the adopted plan's quality and, on Auto, the kept estimate without a source. None before a plan
    /// is adopted.
    pub fn client_replan(&self, operation: String) -> Option<Replan> {
        self.current.as_ref().map(|state| Replan {
            operation,
            quality_preference: state.plan.quality.clone(),
            bandwidth_estimate_kbps: if state.plan.is_auto() {
                self.kept_estimate_kbps
            } else {
                None
            },
            bandwidth_estimate_source: None,
            learned_limit: None,
            observed_throughput_kbps: None,
        })
    }

    /// Up Next, Options or a track menu covers the pill, or the player cannot show pills: one that
    /// would appear waits, one already up hides with its timer frozen; either shows again when this
    /// clears, if the plan has not changed.
    pub fn set_competing_pill_visible(&mut self, visible: bool) {
        if visible == self.competing_pill_visible {
            return;
        }
        self.competing_pill_visible = visible;
        self.refresh_pill();
    }

    /// A timer the host scheduled for a [`Step::Schedule`] fired.
    pub fn timer_fired(&mut self, timer: Timer) {
        self.armed[timer.index()] = false;
        match timer {
            Timer::LongStall => self.long_stall_timer_fired(),
            Timer::Suppression => self.evaluate_long_stall(),
            Timer::Rearm => self.rearm(),
            Timer::Pill => self.pill_expired(),
        }
    }

    // ---- Trigger ----

    fn evaluate(&mut self, now: i64) {
        if self.is_paused || self.transport_trouble {
            return;
        }
        let Some(state) = &mut self.current else {
            return;
        };
        state
            .ended_stalls
            .retain(|stall| now - stall.at < STALL_WINDOW_MS);
        if !triggered(state, now) {
            return;
        }
        let state = self.current.as_ref().expect("checked above");
        // Before anything of the attempt played, the wait is the network's only on evidence.
        if self.attempt_stage != AttemptStage::Played && !self.arrives_slowly(&state.plan) {
            return;
        }
        let evidence_kbps = if self.attempt_stage == AttemptStage::Played {
            None
        } else {
            self.observed_throughput_kbps(&state.plan)
        };

        // A suppressed trigger leaves history intact for a later evaluation.
        if self.answered
            || self.pick_pending
            || self.armed[Timer::Rearm.index()]
            || self.presented.is_some()
        {
            return;
        }
        if let Some(until) = state.cooldown_until
            && now < until
        {
            self.watch_stall(until);
            return;
        }
        if self.inputs.mode == Mode::Never {
            if let Some(state) = &mut self.current {
                state.action = Action::Ignored;
            }
            return;
        }
        let plan = &state.plan;
        let has_lower_rung = lower_rung(plan).is_some();
        let is_auto = plan.is_auto();
        let delivered = plan.delivered_kbps;
        if !has_lower_rung {
            self.cant_lower();
            return;
        }
        if is_auto {
            let Some(estimate) = step_down_estimate(delivered) else {
                self.cant_lower();
                return;
            };
            if self.inputs.mode == Mode::Always {
                self.send_replan(
                    Action::Lowered,
                    AUTO_QUALITY.to_owned(),
                    Some(estimate),
                    None,
                    delivered,
                    evidence_kbps,
                );
                return;
            }
        }
        if let Some(offered) = self.last_offer_at
            && now - offered < REOFFER_MIN_MS
        {
            self.watch_stall(offered + REOFFER_MIN_MS);
            return;
        }
        self.present(PillKind::Prompt);
    }

    /// Keeps watching a long stall a time-based suppression held back: evaluates again when it expires.
    fn watch_stall(&mut self, deadline: i64) {
        self.cancel(Timer::Suppression);
        if self
            .current
            .as_ref()
            .is_none_or(|state| state.stall.is_none())
        {
            return;
        }
        self.schedule(Timer::Suppression, deadline);
    }

    /// Evaluates only if a stall in progress has reached LONG_STALL_MS.
    fn evaluate_long_stall(&mut self) {
        let now = self.now();
        if self
            .timed_from()
            .is_some_and(|from| now - from >= LONG_STALL_MS)
        {
            self.evaluate(now);
        }
    }

    fn timed_from(&self) -> Option<i64> {
        self.current
            .as_ref()
            .and_then(|state| state.stall)
            .and_then(|stall| stall.timed_from)
    }

    /// Evidence the plan's media arrives too slowly: its measured throughput below its delivered bitrate.
    fn arrives_slowly(&self, plan: &Plan) -> bool {
        self.observed_throughput_kbps(plan).is_some()
    }

    /// The figure a lowering sends as observed_throughput_kbps: the measured rate when below the
    /// delivered bitrate.
    fn observed_throughput_kbps(&self, plan: &Plan) -> Option<i32> {
        match (plan.delivered_kbps, self.inputs.measured_kbps) {
            (Some(delivered), Some(measured)) if measured < delivered => Some(measured),
            _ => None,
        }
    }

    /// No lower rung: the display-only notice, once per title.
    fn cant_lower(&mut self) {
        if let Some(state) = &mut self.current {
            state.action = Action::CantLower;
        }
        if self.no_lower_rung_notice_shown {
            return;
        }
        self.no_lower_rung_notice_shown = true;
        self.steps.push(Step::Notice {
            notice: Notice::CantLower,
        });
    }

    // ---- Replans ----

    fn send_replan(
        &mut self,
        action: Action,
        quality_preference: String,
        estimate_kbps: Option<i32>,
        learned_limit: Option<&'static str>,
        from_kbps: Option<i32>,
        observed_throughput_kbps: Option<i32>,
    ) {
        let now = self.now();
        if let Some(state) = &mut self.current {
            state.action = action;
        }
        let sends = self.inputs.sends_fields;
        let request = Replan {
            operation: QUALITY_CHANGE_OPERATION.to_owned(),
            quality_preference,
            bandwidth_estimate_kbps: estimate_kbps,
            bandwidth_estimate_source: estimate_kbps
                .filter(|_| sends)
                .map(|_| OBSERVED_REBUFFER_SOURCE),
            learned_limit: learned_limit.filter(|_| sends),
            observed_throughput_kbps: observed_throughput_kbps.filter(|_| sends),
        };
        // Only an Auto lowering sends an estimate; the next one replaces it.
        let prior_kept = self.kept_estimate_kbps;
        if estimate_kbps.is_some() {
            self.kept_estimate_kbps = estimate_kbps;
        }
        // A Restore's plan settles nothing.
        self.pending_replan = (action != Action::Restored).then_some(PendingReplan {
            automatic: action == Action::Lowered,
            from_kbps,
            prior_kept_estimate_kbps: prior_kept,
        });
        // Triggers on this plan wait for the adoption, or for the rearm if the replan fails or never arrives.
        self.cancel(Timer::Suppression);
        self.cancel(Timer::Rearm);
        self.schedule(Timer::Rearm, now + REARM_AFTER_UNADOPTED_MS);
        self.steps.push(Step::Replan { replan: request });
    }

    fn rearm(&mut self) {
        self.pending_replan = None;
        self.clear_history(self.now());
    }

    fn settle(&mut self, pending: PendingReplan, now: i64) {
        let Some(state) = &mut self.current else {
            return;
        };
        let plan = &state.plan;
        let lower = if plan.quality_reduction_unavailable {
            false
        }
        // A result with no delivered bitrate counts as lower (the vectors' delivered_kbps: 0).
        else if let Some(from) = pending.from_kbps {
            plan.delivered_kbps.unwrap_or(0) < from
        } else {
            return;
        };
        // The viewer's pending pick replaces this plan, so its result shows nothing; its action and cooldown stand.
        if lower {
            if self.pick_pending {
                return;
            }
            if pending.automatic {
                self.present(PillKind::Undo);
            } else {
                self.steps.push(Step::Notice {
                    notice: Notice::Lowered,
                });
            }
        } else {
            state.cooldown_until = Some(now + NO_LOWER_COOLDOWN_MS);
            state.action = Action::CantLower;
            if self.pick_pending {
                return;
            }
            // The viewer has now seen this copy for the title.
            self.no_lower_rung_notice_shown = true;
            self.steps.push(Step::Notice {
                notice: Notice::CantLower,
            });
        }
    }

    // ---- Stall tracker ----

    fn begin_stall(&mut self, now: i64) {
        let transport_trouble = self.transport_trouble;
        let Some(state) = &mut self.current else {
            return;
        };
        state.stall_ended_by_reload = false;
        // The plan's first wait, when it comes before a frame, starts the grace.
        let grace_end = state.grace_ends_at.unwrap_or(now + STARTUP_GRACE_MS);
        state.grace_ends_at = Some(grace_end);
        let mut stall = Stall::default();
        if !transport_trouble {
            // A stall that begins in the grace counts from its end.
            let from = now.max(grace_end);
            stall.timed_from = Some(from);
            stall.measured_from = Some(from);
        }
        state.stall = Some(stall);
        self.schedule_long_stall_timer();
    }

    /// Ends a stall in progress. Only one playback resumed from, outside transport trouble, can be
    /// counted; its time feeds the summary either way. One a source reload ended counts in the
    /// summary if the plan ends before it runs again.
    fn end_stall(&mut self, now: i64, resumed: bool, by_reload: bool) {
        let transport_trouble = self.transport_trouble;
        let Some(state) = &mut self.current else {
            return;
        };
        let Some(stall) = state.stall.take() else {
            return;
        };
        state.record_stall(stall.measured(now));
        if let Some(from) = stall.timed_from
            && !transport_trouble
            && now - from >= STALL_MIN_MS
        {
            if resumed {
                state.ended_stalls.push(EndedStall {
                    at: now,
                    long: now - from >= LONG_STALL_MS,
                });
                state.stall_count += 1;
            } else if by_reload {
                state.stall_ended_by_reload = true;
            }
        }
        self.cancel_stall_timers();
    }

    /// Prompt timeout, rearm, or a quality change: history clears with the grace over; a stall in
    /// progress is retimed from now.
    fn clear_history(&mut self, now: i64) {
        let Some(state) = &mut self.current else {
            return;
        };
        state.ended_stalls.clear();
        state.grace_ends_at = Some(now);
        if let Some(stall) = &mut state.stall
            && stall.timed_from.is_some()
        {
            stall.timed_from = Some(now);
        }
        self.cancel(Timer::Suppression);
        self.schedule_long_stall_timer();
    }

    fn schedule_long_stall_timer(&mut self) {
        self.cancel(Timer::LongStall);
        let Some(from) = self.timed_from() else {
            return;
        };
        self.schedule(Timer::LongStall, from + LONG_STALL_MS);
    }

    fn long_stall_timer_fired(&mut self) {
        let Some(from) = self.timed_from() else {
            return;
        };
        // A timer can fire a little early against the clock; wait out the rest.
        if self.now() - from < LONG_STALL_MS {
            self.schedule_long_stall_timer();
            return;
        }
        self.evaluate(self.now());
    }

    fn cancel_stall_timers(&mut self) {
        self.cancel(Timer::LongStall);
        self.cancel(Timer::Suppression);
    }

    fn leave_playing(&mut self, now: i64) {
        let Some(since) = self.playing_since else {
            return;
        };
        if let Some(state) = &mut self.current {
            state.played_ms += now - since;
        }
        self.playing_since = None;
    }

    /// Closes the playing plan's books, takes down its pill and timers, and emits its summary.
    fn end_plan(&mut self, now: i64) {
        let Some(state) = &self.current else {
            return;
        };
        let summary = self.summary(state, now);
        self.current = None;
        self.cancel_stall_timers();
        self.cancel(Timer::Rearm);
        self.withdraw_pill();
        self.steps.push(Step::Summary { summary });
    }

    fn summary(&self, state: &PlanState, now: i64) -> Summary {
        let played = state.played_ms + self.playing_since.map_or(0, |since| now - since);
        let mut count = state.stall_count;
        let mut total = state.stall_total_ms;
        let mut longest = state.stall_longest_ms;
        if let Some(stall) = &state.stall {
            // A stall still running when its plan ends ends with it, and counts if it reached STALL_MIN_MS.
            let measured = stall.measured(now);
            if measured > 0 {
                total += measured;
                longest = longest.max(measured);
            }
            if !self.transport_trouble
                && stall
                    .timed_from
                    .is_some_and(|from| now - from >= STALL_MIN_MS)
            {
                count += 1;
            }
        } else if state.stall_ended_by_reload {
            count += 1;
        }
        Summary {
            plan_id: state.plan.id.clone(),
            played_ms: played,
            stall_count: count,
            stall_total_ms: total,
            stall_longest_ms: longest,
            delivered_kbps: state.plan.delivered_kbps,
            mode: self.inputs.mode,
            action: state.action,
            bandwidth_estimate_kbps: state.plan.requested_bandwidth_estimate_kbps,
        }
    }

    // ---- Pill ----

    fn present(&mut self, kind: PillKind) {
        self.presented = Some(PresentedPill {
            kind,
            remaining_ms: PROMPT_MS,
            deadline_ms: None,
            first_shown_at: None,
            is_on_screen: false,
        });
        self.refresh_pill();
    }

    fn withdraw_pill(&mut self) {
        self.presented = None;
        self.refresh_pill();
    }

    /// Shows, hides, runs or freezes the pill to match the player. Its timer runs only while it is on
    /// screen and playback is not paused. It counts as shown each time it reaches the screen, and the
    /// prompt as offered the first time.
    fn refresh_pill(&mut self) {
        let Some(mut shown) = self.presented else {
            self.cancel(Timer::Pill);
            self.pill = None;
            return;
        };
        let now = self.now();
        let visible = !self.competing_pill_visible;
        let running = visible && !self.is_paused;
        if !running && let Some(deadline) = shown.deadline_ms {
            shown.remaining_ms = (deadline - now).max(0);
            shown.deadline_ms = None;
            self.cancel(Timer::Pill);
        } else if running && shown.deadline_ms.is_none() {
            let due = now + shown.remaining_ms;
            shown.deadline_ms = Some(due);
            self.schedule(Timer::Pill, due);
        }
        let appearing = visible && !shown.is_on_screen;
        if appearing {
            shown.first_shown_at.get_or_insert(now);
        }
        shown.is_on_screen = visible;
        self.presented = Some(shown);
        self.pill = visible.then_some(Pill {
            kind: shown.kind,
            deadline_ms: shown.deadline_ms,
            remaining_ms: shown.remaining_ms,
        });
        if appearing {
            self.steps.push(Step::PillShown { pill: shown.kind });
        }
    }

    /// The prompt's timeout leaves the title unanswered, clears history and starts the re-offer wait;
    /// the undo's only hides it.
    fn pill_expired(&mut self) {
        let Some(expired) = self.presented else {
            return;
        };
        self.withdraw_pill();
        if expired.kind == PillKind::Prompt {
            if let Some(state) = &mut self.current {
                state.action = Action::TimedOut;
            }
            // The re-offer wait counts from the prompt's first display.
            self.last_offer_at = expired.first_shown_at;
            self.clear_history(self.now());
        } else {
            self.evaluate_long_stall();
        }
    }

    // ---- Helpers ----

    fn schedule(&mut self, timer: Timer, at: i64) {
        self.armed[timer.index()] = true;
        self.steps.push(Step::Schedule { timer, at });
    }

    fn cancel(&mut self, timer: Timer) {
        if std::mem::take(&mut self.armed[timer.index()]) {
            self.steps.push(Step::Cancel { timer });
        }
    }
}

fn triggered(state: &PlanState, now: i64) -> bool {
    if state
        .stall
        .and_then(|stall| stall.timed_from)
        .is_some_and(|from| now - from >= LONG_STALL_MS)
    {
        return true;
    }
    let mut recent = 0;
    for stall in &state.ended_stalls {
        if now - stall.at < STALL_WINDOW_MS {
            if stall.long {
                return true;
            }
            recent += 1;
        }
    }
    recent >= STALLS_BEFORE_TRIGGER
}

/// The spec's lower rung: the highest real rung below the lower of the named rung's bitrate and the
/// delivered bitrate, or below the delivered bitrate when the plan names no rung. Equal bitrates keep
/// the earlier entry. Returns its index in `available_qualities`.
pub fn lower_rung_index(plan: &Plan) -> Option<usize> {
    let ceiling = active_bitrate(plan)?;
    let mut best: Option<(usize, i32)> = None;
    for (index, rung) in plan.available_qualities.iter().enumerate() {
        if rung.label != AUTO_QUALITY
            && rung.bitrate_kbps > 0
            && rung.bitrate_kbps < ceiling
            && best.is_none_or(|(_, bitrate)| rung.bitrate_kbps > bitrate)
        {
            best = Some((index, rung.bitrate_kbps));
        }
    }
    best.map(|(index, _)| index)
}

pub fn lower_rung(plan: &Plan) -> Option<&Rung> {
    lower_rung_index(plan).map(|index| &plan.available_qualities[index])
}

/// `floor(delivered × 7 / 10)` in integer arithmetic, or none below MIN_ESTIMATE_KBPS (22120 → 15484).
/// STEP_DOWN_RATIO is 0.7 as the vectors state it.
pub fn step_down_estimate(delivered_kbps: Option<i32>) -> Option<i32> {
    let delivered = delivered_kbps.filter(|&delivered| delivered > 0)?;
    let estimate = (i64::from(delivered) * 7 / 10) as i32;
    (estimate >= MIN_ESTIMATE_KBPS).then_some(estimate)
}

fn active_bitrate(plan: &Plan) -> Option<i32> {
    let rung = plan.active_quality.as_deref().and_then(|named| {
        plan.available_qualities
            .iter()
            .find(|entry| entry.label == named && entry.bitrate_kbps > 0)
    });
    match rung {
        None => plan.delivered_kbps,
        // A cap that keeps the rung's resolution can deliver below its bitrate.
        Some(rung) => Some(
            rung.bitrate_kbps
                .min(plan.delivered_kbps.unwrap_or(rung.bitrate_kbps)),
        ),
    }
}

// ---- Signals ----

/// One controller call a snapshot asks for (C# `BufferingSignal`, same order).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Signal {
    TroubleStarted,
    TroubleEnded,
    Playing,
    WaitingNetwork,
    WaitingSeek,
    WaitingReload,
    Paused,
}

/// What the player page knows about playback at one moment (C# `BufferingSnapshot`).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Snapshot {
    /// A mount is open (its source loaded in the backend).
    pub has_mount: bool,
    /// The viewer's transport intent: play, not pause. During a remount, the intent the remount opens with.
    pub wants_to_play: bool,
    /// The backend reports Playing and the clock has moved since the mount opened.
    pub playing: bool,
    /// A seek, skip, scrub commit or seek reanchor has not landed yet.
    pub seek_in_flight: bool,
    /// The media ended.
    pub reached_end: bool,
    /// No media bytes arrived for the plan's silence limit (C# `MediaSilence`).
    pub upstream_silent: bool,
}

impl Snapshot {
    /// The handle ABI's bit layout: has_mount 1, wants_to_play 2, playing 4, seek_in_flight 8,
    /// reached_end 16, upstream_silent 32.
    pub fn from_bits(bits: u32) -> Self {
        Self {
            has_mount: bits & 1 != 0,
            wants_to_play: bits & 2 != 0,
            playing: bits & 4 != 0,
            seek_in_flight: bits & 8 != 0,
            reached_end: bits & 16 != 0,
            upstream_silent: bits & 32 != 0,
        }
    }
}

/// Turns the page's playback facts into the controller's media events, so only the network's own
/// buffering can count as a stall (the spec's "Stall" term and its exclusions). A port of silo-apple's
/// BufferingSignalMapper.swift for Bale's backends (IMFMediaEngine and MediaPlayer report Playing /
/// Buffering / Paused / Opening; both raise BufferingStarted/Ended with the same meaning). A repeated
/// state returns nothing, and a change of transport trouble comes before the media call it accompanies.
///
/// Not ported: renderer waits. Neither backend reports how much media is buffered past the playhead,
/// so a wait with ample media buffered (a decoder behind, not the network) is still a network wait; the
/// spec's AVPlayer route has the same gap. Offline detection is left to the silence rule.
#[derive(Debug, Clone)]
pub struct Signals {
    state: Option<Signal>,
    trouble: bool,
    seek_since_playing: bool,
    start_pending: bool,
    plan_has_run: bool,
    media_request_failed: bool,
    media_data_arrived: bool,
    outage_awaits_data: bool,
}

impl Default for Signals {
    fn default() -> Self {
        Self::new()
    }
}

/// At most two calls: a change of transport trouble, then the media call.
pub type SignalCalls = ([Option<Signal>; 2], usize);

impl Signals {
    pub fn new() -> Self {
        Self {
            state: None,
            trouble: false,
            seek_since_playing: false,
            start_pending: false,
            plan_has_run: true,
            media_request_failed: false,
            media_data_arrived: false,
            outage_awaits_data: false,
        }
    }

    /// The adopted plan has run since its adoption.
    pub fn plan_has_run(&self) -> bool {
        self.plan_has_run
    }

    /// A media request failed (a broken progressive body, a failed segment): an outage begins.
    pub fn note_media_request_failed(&mut self) {
        self.media_request_failed = true;
    }

    /// The plan's media received bytes: an outage ends once nothing else holds it.
    pub fn note_media_data_arrived(&mut self) {
        self.media_data_arrived = true;
    }

    /// Forgets the state last reported, so the next update reports it again.
    pub fn invalidate(&mut self) {
        self.state = None;
    }

    /// A plan was adopted: its start is timed until it first runs or pauses, and it starts with no outage.
    pub fn plan_adopted(&mut self) {
        self.state = None;
        self.start_pending = true;
        self.plan_has_run = false;
        self.seek_since_playing = false;
        self.outage_awaits_data = false;
        self.media_request_failed = false;
        self.media_data_arrived = false;
    }

    pub fn update(&mut self, snapshot: Snapshot) -> SignalCalls {
        let mut calls = ([None; 2], 0);
        let mut push = |signal| {
            calls.0[calls.1] = Some(signal);
            calls.1 += 1;
        };
        if self.plan_has_run && snapshot.seek_in_flight {
            self.seek_since_playing = true;
        }
        if !snapshot.wants_to_play {
            self.start_pending = false;
        }
        let mut next = self.media_state(snapshot);
        // Buffering after a seek stays the seek's until playback runs, even once the seek has landed.
        if next == Signal::WaitingNetwork && self.seek_since_playing {
            next = Signal::WaitingSeek;
        }
        if next == Signal::Playing {
            self.seek_since_playing = false;
            self.start_pending = false;
            self.plan_has_run = true;
        }
        let trouble = self.outage_goes_on(snapshot, next);
        if trouble != self.trouble {
            self.trouble = trouble;
            push(if trouble {
                Signal::TroubleStarted
            } else {
                Signal::TroubleEnded
            });
        }
        if Some(next) != self.state {
            self.state = Some(next);
            push(next);
        }
        calls
    }

    /// An outage begins when a media request fails or playback waits while the origin has sent nothing
    /// for the silence limit. It lasts until the plan's media data arrives again or playback runs; no
    /// timer ends it.
    fn outage_goes_on(&mut self, snapshot: Snapshot, next: Signal) -> bool {
        let data_arrived = std::mem::take(&mut self.media_data_arrived);
        let silent_wait = snapshot.upstream_silent && next == Signal::WaitingNetwork;
        if self.media_request_failed || silent_wait {
            self.media_request_failed = false;
            self.outage_awaits_data = true;
            return true;
        }
        if !self.outage_awaits_data {
            return false;
        }
        if next == Signal::Playing || data_arrived {
            self.outage_awaits_data = false;
            return false;
        }
        true
    }

    /// A reload's wait, or before the plan first runs or pauses, the start's.
    fn reload_wait(&self) -> Signal {
        if self.start_pending {
            Signal::WaitingNetwork
        } else {
            Signal::WaitingReload
        }
    }

    fn media_state(&self, s: Snapshot) -> Signal {
        if !s.has_mount {
            return if s.wants_to_play {
                self.reload_wait()
            } else {
                Signal::Paused
            };
        }
        if s.reached_end || !s.wants_to_play {
            return Signal::Paused;
        }
        if s.seek_in_flight {
            return if self.plan_has_run {
                Signal::WaitingSeek
            } else {
                Signal::WaitingNetwork
            };
        }
        if s.playing {
            return Signal::Playing;
        }
        // Opening, Buffering, or Playing with a clock that has not moved yet: the plan waits for media.
        Signal::WaitingNetwork
    }
}

#[cfg(test)]
mod tests;
