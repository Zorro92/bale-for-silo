//! The player's start budget for one mount: how long a plan's source may take from the start of its
//! mount (stream open, FFmpeg probe, engine or MediaPlayer open) to its first frame before the player
//! gives up on it, so the server can offer another route.
//!
//! silo-apple `b9446d5d` ("fail a direct-play load that never opens so the server can replan") bounds
//! the engine's load with `PlaybackStartDeadline.budgetMs`, the 45 s AetherEngine gives server HLS, and
//! its start deadline then bounds the adopted plan until it first plays. A mount in Bale covers both
//! (it opens and starts in one step), so one budget runs from the mount to its first frame. It is held
//! while the device is offline (an outage never ends playback by itself, and a lost connection is not
//! a failed route) and, once the source has opened, while the viewer holds it paused (Apple's start
//! deadline spends only while the viewer wants to play). While another pipeline change (a seek, track
//! change or replan) owns the mount, a budget that has run out waits for it, as Apple's
//! `checkStartDeadline` re-checks a second later when a replan is in flight.
//!
//! Bookkeeping only, in milliseconds of the host's monotonic clock. The page updates it on its UI
//! tick and on every playback state change; the update that runs it out returns true, once.

/// AetherEngine's server-HLS readiness budget, which silo-apple reuses for every route.
pub const BUDGET_MS: i64 = 45_000;

/// What the page knows about the mount at one moment.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Facts {
    /// The first frame is up: the clock moved while playing, or a paused mount opened.
    pub started: bool,
    /// The mount's start ended some other way: a failure was queued, the mount was replaced, or the
    /// attempt stopped (Back, suspension, error screen).
    pub ended: bool,
    /// The backend opened the source (MediaOpened).
    pub opened: bool,
    /// The viewer holds the opened mount paused.
    pub paused: bool,
    /// The device has no network connection.
    pub offline: bool,
    /// A seek, track change or replan owns the mount: it decides what replaces it.
    pub deferred: bool,
}

impl Facts {
    /// The handle ABI's bit layout: started 1, ended 2, opened 4, paused 8, offline 16, deferred 32.
    pub fn from_bits(bits: u32) -> Self {
        Self {
            started: bits & 1 != 0,
            ended: bits & 2 != 0,
            opened: bits & 4 != 0,
            paused: bits & 8 != 0,
            offline: bits & 16 != 0,
            deferred: bits & 32 != 0,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    /// The mount is opening; the budget runs or is held.
    Opening,
    /// The first frame came in time. Final.
    Ready,
    /// The budget ran out first. Final: a first frame after this is late.
    Expired,
    /// The start ended another way (failure, replacement, Back). Final.
    Ended,
}

#[derive(Debug, Clone)]
pub struct StartBudget {
    budget_ms: i64,
    /// Spent before the current run.
    spent_ms: i64,
    /// When the current run began; `None` while held.
    running_since: Option<i64>,
    state: State,
}

impl StartBudget {
    /// A mount begins at `now`. The budget runs from here; the first update holds it if it must.
    pub fn new(budget_ms: i64, now: i64) -> Self {
        Self {
            budget_ms: budget_ms.max(0),
            spent_ms: 0,
            running_since: Some(now),
            state: State::Opening,
        }
    }

    pub fn state(&self) -> State {
        self.state
    }

    /// Records the facts at `now`. True only on the update that runs the budget out: the caller then
    /// fails the start. A first frame or an end seen in the same update wins over the budget, and a
    /// final state never changes again, so the budget fires at most once and never after the start
    /// is over.
    pub fn update(&mut self, now: i64, facts: Facts) -> bool {
        if self.state != State::Opening {
            return false;
        }
        if facts.started {
            self.state = State::Ready;
            self.hold(now);
            return false;
        }
        if facts.ended {
            self.state = State::Ended;
            self.hold(now);
            return false;
        }
        let running = !facts.offline && !(facts.opened && facts.paused);
        if running {
            if self.running_since.is_none() {
                self.running_since = Some(now);
            }
        } else {
            self.hold(now);
        }
        if facts.deferred || !self.has_passed(now) {
            return false;
        }
        self.state = State::Expired;
        self.hold(now);
        true
    }

    /// What is left of the budget at `now`, never negative (for the log and the debug server).
    pub fn remaining_ms(&self, now: i64) -> i64 {
        (self.budget_ms - self.spent_at(now)).max(0)
    }

    /// The budget has run out while running. A held budget never passes: its time is not counted.
    fn has_passed(&self, now: i64) -> bool {
        self.running_since.is_some() && self.spent_at(now) >= self.budget_ms
    }

    fn spent_at(&self, now: i64) -> i64 {
        self.spent_ms
            + self
                .running_since
                .map_or(0, |since| now.saturating_sub(since).max(0))
    }

    fn hold(&mut self, now: i64) {
        if let Some(since) = self.running_since.take() {
            self.spent_ms += now.saturating_sub(since).max(0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const OPENING: Facts = Facts {
        started: false,
        ended: false,
        opened: false,
        paused: false,
        offline: false,
        deferred: false,
    };

    #[test]
    fn a_mount_that_never_opens_runs_out_once_at_the_budget() {
        let mut budget = StartBudget::new(BUDGET_MS, 1_000);
        assert!(!budget.update(1_000 + BUDGET_MS - 1, OPENING));
        assert_eq!(budget.remaining_ms(1_000 + BUDGET_MS - 1), 1);
        assert!(budget.update(1_000 + BUDGET_MS, OPENING));
        assert_eq!(budget.state(), State::Expired);
        assert!(
            !budget.update(1_000 + BUDGET_MS + 100, OPENING),
            "fires once"
        );
        assert_eq!(budget.remaining_ms(1_000 + BUDGET_MS + 100), 0);
    }

    #[test]
    fn a_first_frame_at_the_due_moment_wins() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let started = Facts {
            started: true,
            opened: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS, started));
        assert_eq!(budget.state(), State::Ready);
        assert!(!budget.update(BUDGET_MS * 2, OPENING), "ready is final");
    }

    #[test]
    fn a_first_frame_after_the_budget_ran_out_is_late() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        assert!(budget.update(BUDGET_MS, OPENING));
        let started = Facts {
            started: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS + 1, started));
        assert_eq!(
            budget.state(),
            State::Expired,
            "the failure keeps the mount"
        );
    }

    #[test]
    fn a_failure_back_or_suspension_ends_the_start_without_firing() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let ended = Facts {
            ended: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS, ended), "the end wins a tie");
        assert_eq!(budget.state(), State::Ended);
        assert!(!budget.update(BUDGET_MS * 3, OPENING));
    }

    #[test]
    fn an_outage_holds_the_budget() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let offline = Facts {
            offline: true,
            ..OPENING
        };
        assert!(!budget.update(10_000, offline));
        assert!(
            !budget.update(600_000, offline),
            "ten minutes offline spend nothing"
        );
        assert!(!budget.update(600_000, OPENING));
        assert!(!budget.update(634_999, OPENING));
        assert!(
            budget.update(635_000, OPENING),
            "35 s left after the outage"
        );
    }

    #[test]
    fn a_held_budget_never_fires_even_when_it_was_due() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let offline = Facts {
            offline: true,
            ..OPENING
        };
        // Offline exactly when it falls due: the outage holds it.
        assert!(!budget.update(BUDGET_MS, offline));
        assert!(!budget.update(BUDGET_MS * 10, offline));
        assert!(budget.update(BUDGET_MS * 10, OPENING));
    }

    #[test]
    fn a_pause_holds_only_an_opened_mount() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        // Opening does not wait for the viewer's intent.
        let paused_opening = Facts {
            paused: true,
            ..OPENING
        };
        assert!(!budget.update(20_000, paused_opening));
        let paused_opened = Facts {
            opened: true,
            paused: true,
            ..OPENING
        };
        assert!(!budget.update(30_000, paused_opened));
        assert!(
            !budget.update(3_600_000, paused_opened),
            "an hour paused spends nothing"
        );
        let opened = Facts {
            opened: true,
            ..OPENING
        };
        assert!(!budget.update(3_600_000, opened));
        assert!(!budget.update(3_614_999, opened));
        assert!(budget.update(3_615_000, opened));
    }

    #[test]
    fn a_seek_or_replan_in_flight_defers_the_failure_until_it_lets_go() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let deferred = Facts {
            deferred: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS, deferred));
        assert!(!budget.update(BUDGET_MS + 5_000, deferred));
        assert_eq!(budget.state(), State::Opening);
        assert!(budget.update(BUDGET_MS + 5_100, OPENING));
    }

    #[test]
    fn a_seek_that_lands_the_first_frame_ends_the_deferral_as_ready() {
        let mut budget = StartBudget::new(BUDGET_MS, 0);
        let deferred = Facts {
            deferred: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS + 1, deferred));
        let started = Facts {
            started: true,
            deferred: true,
            ..OPENING
        };
        assert!(!budget.update(BUDGET_MS + 2, started));
        assert_eq!(budget.state(), State::Ready);
    }

    #[test]
    fn a_backward_clock_spends_nothing() {
        let mut budget = StartBudget::new(BUDGET_MS, 10_000);
        assert!(!budget.update(5_000, OPENING));
        assert_eq!(budget.remaining_ms(5_000), BUDGET_MS);
    }

    #[test]
    fn the_bits_name_each_fact() {
        assert_eq!(
            Facts::from_bits(1 | 4 | 32),
            Facts {
                started: true,
                opened: true,
                deferred: true,
                ..OPENING
            }
        );
        assert_eq!(
            Facts::from_bits(2 | 8 | 16),
            Facts {
                ended: true,
                paused: true,
                offline: true,
                ..OPENING
            }
        );
    }
}
