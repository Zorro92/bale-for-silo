//! HLS through a server outage (hls.rs): how long the reader keeps retrying a playlist, init or segment
//! request, or a segment cut mid-body, while the player plays what it already buffered.
//!
//! The web player's reconnect cycle (silo-server `web/src/player/hooks/usePlaybackSession.ts`, with
//! `40c093c` "keep playing buffered HLS through a server outage"; docs/architecture/playback-protocol-v3.md
//! §6.2): waits of 1 s doubling to a 15 s cap, at most 12 attempts (about two and a quarter minutes), and a
//! cycle that starts within 30 s of the previous one recovering continues that cycle's attempts, so a
//! server that answers but keeps dropping cannot keep the player retrying forever. Times are milliseconds
//! on the caller's monotonic clock.

/// The cycle's limits: [`Policy::WEB`] in the app; tests scale them down.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Policy {
    pub base_delay_ms: u64,
    pub max_delay_ms: u64,
    pub max_attempts: u32,
    pub stable_ms: u64,
}

impl Policy {
    /// `RECONNECT_BASE_DELAY_MS`, `RECONNECT_MAX_DELAY_MS`, `RECONNECT_MAX_ATTEMPTS`, `RECONNECT_STABLE_MS`.
    pub const WEB: Policy = Policy {
        base_delay_ms: 1_000,
        max_delay_ms: 15_000,
        max_attempts: 12,
        stable_ms: 30_000,
    };

    /// `reconnectDelayMs(attempt)`: the wait before attempt `attempt` (0-based).
    fn delay(&self, attempt: u32) -> u64 {
        self.base_delay_ms
            .saturating_mul(1u64 << attempt.min(32))
            .min(self.max_delay_ms)
    }
}

#[derive(Debug)]
pub struct OutageBudget {
    policy: Policy,
    /// Attempts the current (or last) cycle has made.
    attempts: u32,
    /// A cycle is running: a request failed and none has succeeded since.
    active: bool,
    recovered_ms: Option<u64>,
}

impl OutageBudget {
    pub fn new(policy: Policy) -> Self {
        OutageBudget {
            policy,
            attempts: 0,
            active: false,
            recovered_ms: None,
        }
    }

    /// A request failed in a way the server can recover from: the wait before the next attempt, or None
    /// when the cycle's attempts are spent.
    pub fn failed(&mut self, now_ms: u64) -> Option<u64> {
        if !self.active {
            self.active = true;
            let stable = self
                .recovered_ms
                .is_none_or(|recovered| now_ms.saturating_sub(recovered) >= self.policy.stable_ms);
            if stable {
                self.attempts = 0;
            }
        }
        if self.attempts >= self.policy.max_attempts {
            return None;
        }
        let delay = self.policy.delay(self.attempts);
        self.attempts += 1;
        Some(delay)
    }

    /// A request succeeded: a running cycle recovered.
    pub fn succeeded(&mut self, now_ms: u64) {
        if self.active {
            self.active = false;
            self.recovered_ms = Some(now_ms);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_waits_follow_the_web_and_stop_after_twelve_attempts() {
        let mut budget = OutageBudget::new(Policy::WEB);
        let mut now = 0;
        let mut waits = Vec::new();
        while let Some(wait) = budget.failed(now) {
            waits.push(wait);
            now += wait;
        }
        assert_eq!(
            waits,
            [
                1_000, 2_000, 4_000, 8_000, 15_000, 15_000, 15_000, 15_000, 15_000, 15_000, 15_000,
                15_000
            ]
        );
        // About two and a quarter minutes.
        assert_eq!(waits.iter().sum::<u64>(), 135_000);
        assert_eq!(budget.failed(now), None);
    }

    #[test]
    fn a_failure_long_after_a_recovery_gets_a_fresh_cycle() {
        let mut budget = OutageBudget::new(Policy::WEB);
        for _ in 0..5 {
            budget.failed(0);
        }
        budget.succeeded(60_000);
        assert_eq!(budget.failed(90_000), Some(1_000));
    }

    #[test]
    fn a_failure_soon_after_a_recovery_continues_the_cycle() {
        let mut budget = OutageBudget::new(Policy::WEB);
        for _ in 0..11 {
            budget.failed(0);
        }
        budget.succeeded(100_000);
        // Dropping again 10 s later: the twelfth attempt is the last.
        assert_eq!(budget.failed(110_000), Some(15_000));
        budget.succeeded(130_000);
        assert_eq!(budget.failed(140_000), None);
        // A success without a running cycle changes nothing.
        let mut idle = OutageBudget::new(Policy::WEB);
        idle.succeeded(5_000);
        assert_eq!(idle.failed(6_000), Some(1_000));
    }
}
