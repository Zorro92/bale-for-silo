//! The player's seek watchdog: how long a local seek (the player's own clock moved to a target in the
//! mounted source, native or FFmpeg) may go without landing before the player stops waiting for it.
//!
//! A seek that never reaches the source (a decoder or wrapper that stopped answering) otherwise leaves
//! the viewer on a frozen picture until Back. AetherEngine bounds both of its seek paths: its native
//! host gives a seek `nativeSeekReconcileBudgetSeconds` (8 s, extended in 4 s steps only while the
//! target's buffer measurably grows), then re-anchors its producer at the target and re-seeks once,
//! and finally gives up as stalled; its software (FFmpeg) host gives a reposition
//! `seekBudgetSeconds` (30 s) and then fails the item as `softwarePipelineFailed`, which silo-apple
//! sends to the server as an ordinary `failure_recovery`. Bale cannot see a buffer at the target
//! without the source lock, so it takes one fixed window between the two ([`BUDGET_MS`]), then makes
//! the same two moves: the first stuck seek of an attempt remounts the same plan at the target (Bale's
//! re-anchor: a fresh source opened there, bounded by the new mount's own start budget), and any
//! later stuck seek fails the mount through the single recovery replan.
//!
//! A seek is watched from the moment the page issues it until it lands: the backend reported the seek
//! complete, or the player clock reads near the target (Apple's landing tolerance). A newer seek
//! replaces the watch with its own full budget; an ended mount (Back, suspension, a queued failure, a
//! replan, reanchor or track change that replaced it) drops the watch without spending anything. The
//! budget is held while the device is offline, and an expiry waits while another pipeline change
//! holds the player. A seek_reanchor or a forward-HLS remount is a new mount, bounded by its start
//! budget, and is never watched here.
//!
//! Bookkeeping only, in milliseconds of the host's monotonic clock; positions in player seconds.

/// One window for a seek to land: above Apple's 8 s native first window (Bale cannot see the target's
/// buffer growing to extend it), below its 30 s software budget, well above a normal seek (Aether:
/// about 1-2 s) and above Bale's own 10 s skip wait, so a skip that timed out has released the player
/// before the watchdog acts.
pub const BUDGET_MS: i64 = 15_000;

/// The player clock this close to the target counts as a landing even without the backend's
/// completion event (AetherEngine's rendered-time landing window). It counts only for a seek that
/// started farther than this from its target, so a frozen clock never passes for a landing.
pub const LANDING_TOLERANCE_S: f64 = 5.0;

/// What the page knows about the watched seek at one moment.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Facts {
    /// The backend reported the seek complete (SeekCompleted after this seek was issued).
    pub completed: bool,
    /// The seek's mount is gone or stopped: Back, suspension, a queued failure, or a replan, reanchor
    /// or track change replaced it.
    pub ended: bool,
    /// The device has no network connection.
    pub offline: bool,
    /// Another pipeline change holds the player; an expiry waits for it to let go.
    pub deferred: bool,
}

impl Facts {
    /// The handle ABI's bit layout: completed 1, ended 2, offline 4, deferred 8.
    pub fn from_bits(bits: u32) -> Self {
        Self {
            completed: bits & 1 != 0,
            ended: bits & 2 != 0,
            offline: bits & 4 != 0,
            deferred: bits & 8 != 0,
        }
    }
}

/// What the page does after an update.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Action {
    None = 0,
    /// The attempt's first stuck seek: remount the same plan (session, tracks) at the target.
    Remount = 1,
    /// A stuck seek after the remount was spent: fail the mount through the single recovery replan.
    Fail = 2,
}

#[derive(Debug, Clone)]
struct Watch {
    budget_ms: i64,
    from_s: f64,
    target_s: f64,
    /// Spent before the current run.
    spent_ms: i64,
    /// When the current run began; `None` while held.
    running_since: Option<i64>,
}

impl Watch {
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

    /// The clock reads near the target, and the seek began far enough away for that to mean something.
    fn clock_landed(&self, clock_s: f64) -> bool {
        clock_s.is_finite()
            && self.from_s.is_finite()
            && self.target_s.is_finite()
            && (self.from_s - self.target_s).abs() > LANDING_TOLERANCE_S
            && (clock_s - self.target_s).abs() <= LANDING_TOLERANCE_S
    }
}

/// One playback attempt's watchdog: at most one watched seek, and one remount for the attempt.
#[derive(Debug, Clone, Default)]
pub struct SeekWatchdog {
    remount_spent: bool,
    watch: Option<Watch>,
}

impl SeekWatchdog {
    pub fn new() -> Self {
        Self::default()
    }

    /// A local seek from `from_s` to `target_s` (player seconds) was issued at `now`, with `budget_ms`
    /// to land ([`BUDGET_MS`] unless a debug override asks for another). A seek still watched is
    /// superseded: the newer one gets a full budget.
    pub fn begin(&mut self, now: i64, budget_ms: i64, from_s: f64, target_s: f64) {
        self.watch = Some(Watch {
            budget_ms: budget_ms.max(0),
            from_s,
            target_s,
            spent_ms: 0,
            running_since: Some(now),
        });
    }

    /// Records the facts and the player clock (`clock_s`, NaN when unknown) at `now`. Returns the one
    /// action for an expiry; every other update returns [`Action::None`]. A landing or an end seen in
    /// the same update wins over the budget, and the watch is over after any outcome, so one seek
    /// acts at most once.
    pub fn update(&mut self, now: i64, facts: Facts, clock_s: f64) -> Action {
        let Some(watch) = self.watch.as_mut() else {
            return Action::None;
        };
        if facts.completed || facts.ended || watch.clock_landed(clock_s) {
            self.watch = None;
            return Action::None;
        }
        if facts.offline {
            watch.hold(now);
        } else if watch.running_since.is_none() {
            watch.running_since = Some(now);
        }
        if facts.deferred || watch.running_since.is_none() || watch.spent_at(now) < watch.budget_ms
        {
            return Action::None;
        }
        self.watch = None;
        if self.remount_spent {
            Action::Fail
        } else {
            self.remount_spent = true;
            Action::Remount
        }
    }

    /// A seek is being watched.
    pub fn watching(&self) -> bool {
        self.watch.is_some()
    }

    /// The attempt's one remount was used.
    pub fn remount_spent(&self) -> bool {
        self.remount_spent
    }

    /// What is left of the watched seek's budget at `now`, never negative; `None` when idle.
    pub fn remaining_ms(&self, now: i64) -> Option<i64> {
        self.watch
            .as_ref()
            .map(|watch| (watch.budget_ms - watch.spent_at(now)).max(0))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::start_budget;

    const WAITING: Facts = Facts {
        completed: false,
        ended: false,
        offline: false,
        deferred: false,
    };
    const COMPLETED: Facts = Facts {
        completed: true,
        ..WAITING
    };
    const ENDED: Facts = Facts {
        ended: true,
        ..WAITING
    };
    const OFFLINE: Facts = Facts {
        offline: true,
        ..WAITING
    };
    const DEFERRED: Facts = Facts {
        deferred: true,
        ..WAITING
    };
    /// The frozen clock: still at the seek's start.
    const FROM: f64 = 600.0;
    const TARGET: f64 = 630.0;

    fn watching(now: i64) -> SeekWatchdog {
        let mut dog = SeekWatchdog::new();
        dog.begin(now, BUDGET_MS, FROM, TARGET);
        dog
    }

    #[test]
    fn idle_does_nothing() {
        let mut dog = SeekWatchdog::new();
        assert_eq!(dog.update(1_000_000, WAITING, FROM), Action::None);
        assert_eq!(dog.remaining_ms(0), None);
        assert!(!dog.watching());
    }

    #[test]
    fn a_seek_that_lands_in_time_is_forgotten() {
        let mut dog = watching(0);
        assert_eq!(dog.update(1_500, WAITING, FROM), Action::None);
        assert_eq!(dog.update(1_600, COMPLETED, TARGET), Action::None);
        assert!(!dog.watching());
        assert_eq!(dog.update(BUDGET_MS * 4, WAITING, FROM), Action::None);
        assert!(!dog.remount_spent());
    }

    #[test]
    fn the_first_stuck_seek_remounts_once_at_the_budget() {
        let mut dog = watching(1_000);
        assert_eq!(
            dog.update(1_000 + BUDGET_MS - 1, WAITING, FROM),
            Action::None
        );
        assert_eq!(dog.remaining_ms(1_000 + BUDGET_MS - 1), Some(1));
        assert_eq!(
            dog.update(1_000 + BUDGET_MS, WAITING, FROM),
            Action::Remount
        );
        assert!(dog.remount_spent());
        assert!(!dog.watching());
        assert_eq!(
            dog.update(1_000 + BUDGET_MS * 3, WAITING, FROM),
            Action::None,
            "acts once"
        );
    }

    #[test]
    fn a_second_stuck_seek_fails_and_never_loops() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, WAITING, FROM), Action::Remount);
        // The remount played; a later seek gets stuck too.
        dog.begin(100_000, BUDGET_MS, FROM, TARGET);
        assert_eq!(dog.update(100_000 + BUDGET_MS, WAITING, FROM), Action::Fail);
        assert_eq!(dog.update(200_000, WAITING, FROM), Action::None);
        // Even after the failure replan, a third stuck seek only fails again (the page's single replan
        // is spent then, so that shows the error screen): never another remount.
        dog.begin(300_000, BUDGET_MS, FROM, TARGET);
        assert_eq!(dog.update(300_000 + BUDGET_MS, WAITING, FROM), Action::Fail);
    }

    #[test]
    fn a_completion_at_the_deadline_wins() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, COMPLETED, FROM), Action::None);
        assert!(!dog.remount_spent());
        assert!(!dog.watching());
    }

    #[test]
    fn a_clock_at_the_target_is_a_landing_without_the_event() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, WAITING, TARGET + 0.4), Action::None);
        assert!(!dog.watching());
        // Within the tolerance on either side, as Apple's sink accepts.
        let mut dog = watching(0);
        assert_eq!(
            dog.update(BUDGET_MS, WAITING, TARGET - LANDING_TOLERANCE_S),
            Action::None
        );
        assert!(!dog.watching());
    }

    #[test]
    fn a_frozen_clock_near_a_short_seek_is_not_a_landing() {
        let mut dog = SeekWatchdog::new();
        // A 3 s seek: the clock never left the tolerance, so only the event can land it.
        dog.begin(0, BUDGET_MS, 100.0, 103.0);
        assert_eq!(dog.update(BUDGET_MS - 1, WAITING, 100.0), Action::None);
        assert!(dog.watching());
        assert_eq!(dog.update(BUDGET_MS, WAITING, 100.0), Action::Remount);
    }

    #[test]
    fn an_unknown_clock_never_lands() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, WAITING, f64::NAN), Action::Remount);
    }

    #[test]
    fn a_newer_seek_restarts_the_watch() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS - 1_000, WAITING, FROM), Action::None);
        dog.begin(BUDGET_MS - 1_000, BUDGET_MS, FROM, TARGET + 30.0);
        assert_eq!(
            dog.update(BUDGET_MS, WAITING, FROM),
            Action::None,
            "the superseded seek's deadline is gone"
        );
        assert_eq!(
            dog.update(2 * BUDGET_MS - 1_001, WAITING, FROM),
            Action::None
        );
        assert_eq!(
            dog.update(2 * BUDGET_MS - 1_000, WAITING, FROM),
            Action::Remount
        );
    }

    #[test]
    fn a_newer_seek_that_lands_spends_nothing() {
        let mut dog = watching(0);
        dog.begin(14_000, BUDGET_MS, FROM, TARGET + 30.0);
        assert_eq!(dog.update(14_500, COMPLETED, TARGET + 30.0), Action::None);
        assert_eq!(dog.update(BUDGET_MS * 2, WAITING, FROM), Action::None);
        assert!(!dog.remount_spent());
    }

    #[test]
    fn back_suspension_or_a_replacement_drop_the_watch_without_spending() {
        let mut dog = watching(0);
        assert_eq!(
            dog.update(BUDGET_MS, ENDED, FROM),
            Action::None,
            "an end wins a tie"
        );
        assert!(!dog.watching());
        assert!(!dog.remount_spent());
        assert_eq!(dog.update(BUDGET_MS * 5, WAITING, FROM), Action::None);
        // The remount is still there for a later stuck seek.
        dog.begin(BUDGET_MS * 6, BUDGET_MS, FROM, TARGET);
        assert_eq!(dog.update(BUDGET_MS * 7, WAITING, FROM), Action::Remount);
    }

    #[test]
    fn an_outage_holds_the_budget() {
        let mut dog = watching(0);
        assert_eq!(dog.update(5_000, OFFLINE, FROM), Action::None);
        assert_eq!(dog.update(600_000, OFFLINE, FROM), Action::None);
        assert_eq!(dog.remaining_ms(600_000), Some(BUDGET_MS - 5_000));
        assert_eq!(dog.update(600_000, WAITING, FROM), Action::None);
        assert_eq!(dog.update(609_999, WAITING, FROM), Action::None);
        assert_eq!(dog.update(610_000, WAITING, FROM), Action::Remount);
    }

    #[test]
    fn an_expiry_waits_while_another_change_holds_the_player() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, DEFERRED, FROM), Action::None);
        assert_eq!(dog.update(BUDGET_MS + 9_000, DEFERRED, FROM), Action::None);
        assert!(dog.watching());
        // The change that held the player replaced the mount: nothing to recover.
        assert_eq!(dog.update(BUDGET_MS + 9_100, ENDED, FROM), Action::None);
        assert!(!dog.remount_spent());
        // Or it let go with the seek still stuck: act now.
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS + 1, DEFERRED, FROM), Action::None);
        assert_eq!(dog.update(BUDGET_MS + 2, WAITING, FROM), Action::Remount);
        // A landing while deferred still counts.
        let mut dog = watching(0);
        assert_eq!(
            dog.update(
                BUDGET_MS + 1,
                Facts {
                    completed: true,
                    deferred: true,
                    ..WAITING
                },
                FROM
            ),
            Action::None
        );
        assert!(!dog.watching());
    }

    #[test]
    fn a_remount_that_hangs_is_left_to_its_start_budget_and_never_remounts_again() {
        let mut dog = watching(0);
        assert_eq!(dog.update(BUDGET_MS, WAITING, FROM), Action::Remount);
        // The remount is a new mount with its own start budget; the watchdog watches no seek meanwhile.
        let mut remount = start_budget::StartBudget::new(start_budget::BUDGET_MS, BUDGET_MS);
        let opening = start_budget::Facts::default();
        let mut now = BUDGET_MS;
        while now < BUDGET_MS + start_budget::BUDGET_MS {
            assert_eq!(dog.update(now, WAITING, FROM), Action::None);
            assert!(!remount.update(now, opening));
            now += 250;
        }
        assert!(
            remount.update(BUDGET_MS + start_budget::BUDGET_MS, opening),
            "the hung remount fails as a start that ran out (the single replan)"
        );
        assert_eq!(
            dog.update(now + BUDGET_MS * 10, WAITING, FROM),
            Action::None
        );
        // Had the remount played, the next stuck seek would fail, not remount.
        dog.begin(now, BUDGET_MS, FROM, TARGET);
        assert_eq!(dog.update(now + BUDGET_MS, WAITING, FROM), Action::Fail);
    }

    #[test]
    fn each_seek_carries_its_own_budget() {
        let mut dog = SeekWatchdog::new();
        dog.begin(0, 5_000, FROM, TARGET);
        assert_eq!(dog.remaining_ms(0), Some(5_000));
        assert_eq!(dog.update(5_000, WAITING, FROM), Action::Remount);
        // A later seek after the debug override was cleared gets the default.
        dog.begin(10_000, BUDGET_MS, FROM, TARGET);
        assert_eq!(dog.update(15_000, WAITING, FROM), Action::None);
        assert_eq!(dog.update(10_000 + BUDGET_MS, WAITING, FROM), Action::Fail);
    }

    #[test]
    fn a_backward_clock_spends_nothing() {
        let mut dog = watching(10_000);
        assert_eq!(dog.update(5_000, WAITING, FROM), Action::None);
        assert_eq!(dog.remaining_ms(5_000), Some(BUDGET_MS));
    }

    #[test]
    fn a_zero_budget_acts_on_the_first_update() {
        let mut dog = SeekWatchdog::new();
        dog.begin(0, -5, FROM, TARGET);
        assert_eq!(dog.update(0, WAITING, FROM), Action::Remount);
    }

    #[test]
    fn the_bits_name_each_fact() {
        assert_eq!(
            Facts::from_bits(1 | 8),
            Facts {
                completed: true,
                deferred: true,
                ..WAITING
            }
        );
        assert_eq!(
            Facts::from_bits(2 | 4),
            Facts {
                ended: true,
                offline: true,
                ..WAITING
            }
        );
    }
}
