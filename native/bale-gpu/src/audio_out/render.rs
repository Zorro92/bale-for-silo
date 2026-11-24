//! The render thread's platform-independent steps: filling one device period from the ring and judging a
//! sync reference. The Windows thread (`wasapi`) calls exactly these between its COM calls; the tests below
//! drive them with a simulated device.

use super::ring::Ring;
use super::sync::{
    Decision, FillPlan, Reference, Sample, SyncController, frames_at, hns_from_frames,
};
use super::timeline::Timeline;

/// Fills one device period into `out` (`buffer_frames` frames of `block` bytes): carries out pending
/// corrections, copies queued frames, fills the rest with silence and records it all in the timeline.
/// Silence is zero bytes: silence for integer PCM, and a gap between bursts on an IEC 61937 link.
pub fn fill_period(
    ring: &mut Ring,
    timeline: &mut Timeline,
    sync: &mut SyncController,
    out: &mut [u8],
    buffer_frames: u32,
    block: usize,
) -> FillPlan {
    let (pad_pending, drop_pending) = sync.pending();
    let plan = FillPlan::new(buffer_frames, ring.len() as u64, pad_pending, drop_pending);
    ring.discard(plan.dropped);
    let pad_end = plan.pad as usize * block;
    let data_end = pad_end + plan.data as usize * block;
    let period_end = buffer_frames as usize * block;
    out[..pad_end].fill(0);
    timeline.push_silence(u64::from(plan.pad));
    let copied = ring.read(
        &mut out[pad_end..data_end],
        plan.data as usize,
        |frames, pts| timeline.push_data(frames, pts),
    );
    debug_assert_eq!(copied, plan.data as usize);
    out[data_end..period_end].fill(0);
    timeline.push_silence(u64::from(plan.underrun));
    sync.applied(&plan);
    plan
}

/// Judges a caller's `reference`. The measured error is the pts playing at the device position of `sample`
/// (extrapolated to the reference's QPC time) minus the reference's video position. The predicted one is the
/// pts of the next frame to be handed over (`next_pts`, the ring's) minus the video position when it will be
/// heard, once everything already handed to the device has played.
pub fn evaluate(
    sync: &mut SyncController,
    timeline: &Timeline,
    next_pts: Option<i64>,
    sample: &Sample,
    reference: &Reference,
    rate: u32,
) -> Decision {
    let at = frames_at(sample, reference.qpc_hns, rate);
    let (Some(playing), Some(next)) = (timeline.pts_at(at), next_pts) else {
        return sync.unmeasured();
    };
    let heard_in = hns_from_frames(timeline.submitted().saturating_sub(at), rate);
    let measured = playing.saturating_sub(reference.video_hns);
    let predicted = next.saturating_sub(reference.video_hns.saturating_add(heard_in));
    sync.measure(measured, predicted)
}

#[cfg(test)]
mod tests {
    use super::super::sync::{HNS_PER_SECOND, SyncConfig};
    use super::*;

    const RATE: u32 = 48_000;
    const PERIOD: u32 = 480;
    const BLOCK: usize = 4;
    /// Frames between handing a frame to the device and hearing it: one buffer queued, one playing.
    const LATENCY: u64 = 2 * PERIOD as u64;

    /// A writer feeding 10 ms chunks of a counted timeline, and a device taking one period each 10 ms.
    struct Sim {
        ring: Ring,
        timeline: Timeline,
        sync: SyncController,
        written: u64,
        periods: u64,
        underruns: u32,
        silence: u64,
        out: Vec<u8>,
    }

    impl Sim {
        fn new(config: SyncConfig) -> Self {
            Self {
                ring: Ring::new(24_000, BLOCK, RATE),
                timeline: Timeline::new(RATE),
                sync: SyncController::new(config, RATE),
                written: 0,
                periods: 0,
                underruns: 0,
                silence: 0,
                out: vec![0xEE; PERIOD as usize * BLOCK],
            }
        }

        /// Fills the ring with frames whose pts is their index at 48 kHz.
        fn feed(&mut self) {
            let chunk = vec![1u8; PERIOD as usize * BLOCK];
            while self
                .ring
                .write(&chunk, Some(hns_from_frames(self.written, RATE)))
                > 0
            {
                self.written += u64::from(PERIOD);
            }
        }

        fn now(&self) -> i64 {
            self.periods as i64 * hns_from_frames(u64::from(PERIOD), RATE)
        }

        fn sample(&self) -> Sample {
            let frames = self.timeline.submitted().saturating_sub(LATENCY);
            Sample {
                frames,
                hns: hns_from_frames(frames, RATE),
                qpc_hns: self.now(),
            }
        }

        /// One device period; `video_offset` is how far the caller's video clock trails the device clock.
        fn period(&mut self, feed: bool, video_offset: i64) -> Decision {
            if feed {
                self.feed();
            }
            let plan = fill_period(
                &mut self.ring,
                &mut self.timeline,
                &mut self.sync,
                &mut self.out,
                PERIOD,
                BLOCK,
            );
            if plan.underrun > 0 {
                self.underruns += 1;
                self.silence += u64::from(plan.underrun);
            }
            self.periods += 1;
            let sample = self.sample();
            // The video clock ran with the device from its first heard frame.
            let heard = self.now() - hns_from_frames(LATENCY, RATE);
            let reference = Reference {
                video_hns: heard - video_offset,
                qpc_hns: self.now(),
            };
            let next = self.ring.next_pts();
            evaluate(
                &mut self.sync,
                &self.timeline,
                next,
                &sample,
                &reference,
                RATE,
            )
        }

        fn error_ms(&self) -> f64 {
            self.sync.counters.last_error_hns as f64 / 10_000.0
        }
    }

    #[test]
    fn a_period_is_padding_then_queued_frames_then_silence() {
        let mut ring = Ring::new(1_000, BLOCK, RATE);
        let mut timeline = Timeline::new(RATE);
        let mut sync = SyncController::new(SyncConfig::PCM, RATE);
        ring.write(&[7u8; 100 * BLOCK], Some(5 * HNS_PER_SECOND));
        let mut out = vec![0xEE; PERIOD as usize * BLOCK];
        let plan = fill_period(&mut ring, &mut timeline, &mut sync, &mut out, PERIOD, BLOCK);
        assert_eq!((plan.data, plan.underrun), (100, 380));
        assert!(out[..100 * BLOCK].iter().all(|&b| b == 7));
        assert!(
            out[100 * BLOCK..].iter().all(|&b| b == 0),
            "underrun silence"
        );
        assert_eq!(timeline.submitted(), 480);
        assert_eq!(timeline.pts_at(99), Some(5 * HNS_PER_SECOND + 20_625));
        assert_eq!(
            timeline.pts_at(479),
            Some(5 * HNS_PER_SECOND + hns_from_frames(100, RATE))
        );
        // Padding comes first.
        assert_eq!(sync.measure(300_000, 300_000), Decision::Pad(1_440));
        ring.write(&[9u8; 1_000 * BLOCK], None);
        let plan = fill_period(&mut ring, &mut timeline, &mut sync, &mut out, PERIOD, BLOCK);
        assert_eq!((plan.pad, plan.data), (480, 0));
        assert!(out.iter().all(|&b| b == 0));
        assert!(plan.is_silent());
    }

    #[test]
    fn a_fed_stream_in_sync_needs_no_correction() {
        let mut sim = Sim::new(SyncConfig::PCM);
        for _ in 0..500 {
            assert_eq!(sim.period(true, 0), Decision::Keep);
        }
        assert_eq!(sim.underruns, 0);
        assert_eq!(sim.sync.counters.measured, 500);
        assert!(sim.error_ms().abs() < 0.1, "{} ms", sim.error_ms());
    }

    #[test]
    fn audio_ahead_of_the_video_is_padded_once_and_then_holds() {
        let mut sim = Sim::new(SyncConfig::PCM);
        for _ in 0..10 {
            sim.period(true, 0);
        }
        // The video falls 50 ms behind: one 2400-frame pad, then nothing more.
        let mut decisions = Vec::new();
        for _ in 0..100 {
            decisions.push(sim.period(true, 500_000));
        }
        assert_eq!(decisions[0], Decision::Pad(2_400));
        assert!(decisions[1..].iter().all(|d| *d == Decision::Keep));
        assert!(sim.error_ms().abs() < 0.1, "{} ms", sim.error_ms());
        assert_eq!(
            (
                sim.sync.counters.pad_corrections,
                sim.sync.counters.padded_frames
            ),
            (1, 2_400)
        );
        // Padding is not an underrun.
        assert_eq!(sim.underruns, 0);
    }

    #[test]
    fn audio_behind_the_video_drops_frames_and_catches_up() {
        let mut sim = Sim::new(SyncConfig::PCM);
        for _ in 0..10 {
            sim.period(true, 0);
        }
        let mut decisions = Vec::new();
        for _ in 0..100 {
            decisions.push(sim.period(true, -300_000));
        }
        assert_eq!(decisions[0], Decision::Drop(1_440));
        assert!(decisions[1..].iter().all(|d| *d == Decision::Keep));
        assert!(sim.error_ms().abs() < 0.1, "{} ms", sim.error_ms());
        assert_eq!(sim.sync.counters.dropped_frames, 1_440);
    }

    #[test]
    fn an_underrun_plays_silence_counts_it_and_drops_catch_up_while_it_is_still_queued() {
        let mut sim = Sim::new(SyncConfig::PCM);
        for _ in 0..50 {
            sim.period(true, 0);
        }
        // The writer stalls: the 500 ms ring drains, then 10 periods (100 ms) of silence.
        let mut starved = Vec::new();
        while sim.underruns < 10 {
            starved.push(sim.period(false, 0));
            assert!(starved.len() < 100);
        }
        assert_eq!(sim.silence, 10 * u64::from(PERIOD));
        // Each time the queued silence passes 20 ms more, 30 ms of the frames still to come are marked for
        // dropping, before they arrive.
        let drops: Vec<_> = starved
            .iter()
            .filter(|d| matches!(d, Decision::Drop(_)))
            .collect();
        assert_eq!(drops, [&Decision::Drop(1_440); 3]);
        let mut decisions = Vec::new();
        for _ in 0..100 {
            decisions.push(sim.period(true, 0));
        }
        assert!(decisions.iter().all(|d| *d == Decision::Keep));
        // 90 of the 100 ms were dropped as soon as the frames came; the last 10 ms are within the threshold.
        assert_eq!(sim.sync.counters.dropped_frames, 4_320);
        assert_eq!(sim.sync.counters.last_error_hns, -100_000);
        assert_eq!(sim.underruns, 10);
    }

    #[test]
    fn bitstreams_are_measured_but_never_corrected() {
        let mut sim = Sim::new(SyncConfig::BITSTREAM);
        for _ in 0..50 {
            assert_eq!(sim.period(true, 500_000), Decision::Keep);
        }
        assert!((sim.error_ms() - 50.0).abs() < 0.1, "{} ms", sim.error_ms());
        assert_eq!(sim.sync.pending(), (0, 0));
    }

    #[test]
    fn nothing_timed_is_unmeasured() {
        let mut ring = Ring::new(1_000, BLOCK, RATE);
        let mut timeline = Timeline::new(RATE);
        let mut sync = SyncController::new(SyncConfig::PCM, RATE);
        ring.write(&[1u8; 960 * BLOCK], None);
        let mut out = vec![0; PERIOD as usize * BLOCK];
        fill_period(&mut ring, &mut timeline, &mut sync, &mut out, PERIOD, BLOCK);
        let sample = Sample::default();
        let reference = Reference {
            video_hns: 0,
            qpc_hns: 0,
        };
        assert_eq!(
            evaluate(
                &mut sync,
                &timeline,
                ring.next_pts(),
                &sample,
                &reference,
                RATE
            ),
            Decision::Unmeasured
        );
        assert_eq!(sync.counters.unmeasured, 1);
    }
}
