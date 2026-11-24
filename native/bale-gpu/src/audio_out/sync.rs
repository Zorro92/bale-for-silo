//! Clock conversions, the per-period fill plan (silence on underrun, sync corrections) and the controller
//! that slaves the audio to the caller's video clock (`bale_audio_out_sync`).
//!
//! The caller sends a reference now and then: the video position at a QueryPerformanceCounter time. The render
//! thread extrapolates its newest `IAudioClock` position to that QPC time and looks up which pts plays there
//! ([`super::timeline::Timeline`]): that pts minus the video position is the error it reports (positive when
//! the audio is ahead). Corrections act on the frames not yet handed to the device, so the controller judges
//! the error those will have: the next queued frame's pts against the video position when it will be heard
//! (after everything already handed over plays). That prediction already holds the silence and corrections
//! between the speaker and the ring, so an underrun's silence is caught up while it is still queued and a
//! correction is never counted twice. Beyond [`SyncConfig::threshold_hns`] it corrects with whole frames:
//! silence when the audio is ahead, dropped queued frames when it is behind.

/// 100 ns units in a second.
pub const HNS_PER_SECOND: i64 = 10_000_000;

/// `frames` at `rate` in 100 ns, rounded to nearest.
pub fn hns_from_frames(frames: u64, rate: u32) -> i64 {
    let rate = u128::from(rate.max(1));
    let hns = (u128::from(frames) * HNS_PER_SECOND as u128 + rate / 2) / rate;
    hns.min(i64::MAX as u128) as i64
}

/// A signed duration in 100 ns as frames at `rate`, rounded to nearest (halves away from zero).
pub fn frames_from_hns(hns: i64, rate: u32) -> i64 {
    let frames = (u128::from(hns.unsigned_abs()) * u128::from(rate) + HNS_PER_SECOND as u128 / 2)
        / HNS_PER_SECOND as u128;
    let frames = frames.min(i64::MAX as u128) as i64;
    if hns < 0 { -frames } else { frames }
}

/// An `IAudioClock::GetPosition` value (in `GetFrequency` units) as frames at `rate`, rounded down.
pub fn position_frames(position: u64, frequency: u64, rate: u32) -> u64 {
    if frequency == 0 {
        return 0;
    }
    let frames = u128::from(position) * u128::from(rate) / u128::from(frequency);
    frames.min(u64::MAX as u128) as u64
}

/// An `IAudioClock::GetPosition` value in 100 ns.
pub fn position_hns(position: u64, frequency: u64) -> i64 {
    if frequency == 0 {
        return 0;
    }
    let hns = u128::from(position) * HNS_PER_SECOND as u128 / u128::from(frequency);
    hns.min(i64::MAX as u128) as i64
}

/// A QueryPerformanceCounter value in 100 ns, the unit `IAudioClock` gives its QPC position in.
pub fn qpc_hns(ticks: i64, frequency: i64) -> i64 {
    if frequency <= 0 {
        return 0;
    }
    let hns = i128::from(ticks) * i128::from(HNS_PER_SECOND) / i128::from(frequency);
    hns.clamp(i128::from(i64::MIN), i128::from(i64::MAX)) as i64
}

/// The device position, in frames, at QPC time `at_hns`, extrapolated from a position `sample` (the device
/// plays `rate` frames a second while it runs).
pub fn frames_at(sample: &Sample, at_hns: i64, rate: u32) -> u64 {
    let delta = frames_from_hns(at_hns.saturating_sub(sample.qpc_hns), rate);
    sample.frames.saturating_add_signed(delta)
}

/// An `IAudioClock` position reading.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Sample {
    /// Frames since the last reset.
    pub frames: u64,
    /// The same in 100 ns.
    pub hns: i64,
    /// QueryPerformanceCounter time of the reading, in 100 ns.
    pub qpc_hns: i64,
}

/// A caller's sync reference: the video position at a QueryPerformanceCounter time (both in 100 ns).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Reference {
    pub video_hns: i64,
    pub qpc_hns: i64,
}

/// What one device period holds, in order: correction padding, queued frames, underrun silence.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FillPlan {
    /// Queued frames discarded first (a drop correction).
    pub dropped: u64,
    /// Silence first (a pad correction).
    pub pad: u32,
    /// Queued frames played.
    pub data: u32,
    /// Silence because the ring ran dry: an underrun.
    pub underrun: u32,
}

impl FillPlan {
    /// The plan for a period of `buffer_frames` with `queued` frames in the ring and the controller's
    /// pending corrections.
    pub fn new(buffer_frames: u32, queued: u64, pad_pending: u64, drop_pending: u64) -> Self {
        let dropped = drop_pending.min(queued);
        let pad = pad_pending.min(u64::from(buffer_frames)) as u32;
        let data = (queued - dropped).min(u64::from(buffer_frames - pad)) as u32;
        Self {
            dropped,
            pad,
            data,
            underrun: buffer_frames - pad - data,
        }
    }

    /// No queued frame plays this period (the buffer can go out flagged silent).
    pub fn is_silent(&self) -> bool {
        self.data == 0
    }
}

/// How the controller corrects.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SyncConfig {
    /// Errors up to this are left alone.
    pub threshold_hns: i64,
    /// The most one correction moves; a larger error takes several.
    pub max_step_hns: i64,
    /// Errors beyond this come from a reference on another timeline (a seek in flight, a stale clock) and
    /// are never corrected.
    pub range_hns: i64,
    /// Corrections move whole multiples of this many frames; 0 = measure only.
    pub granularity: u32,
}

impl SyncConfig {
    /// PCM: corrections of whole frames beyond 20 ms, at most 1 s at a time, nothing beyond 10 s.
    pub const PCM: Self = Self {
        threshold_hns: 200_000,
        max_step_hns: HNS_PER_SECOND,
        range_hns: 10 * HNS_PER_SECOND,
        granularity: 1,
    };
    /// IEC 61937: measured only. Dropping or padding inside a data burst would break it; corrections of whole
    /// bursts (the packer's repetition period) come with the passthrough feeder.
    pub const BITSTREAM: Self = Self {
        granularity: 0,
        ..Self::PCM
    };

    pub fn for_bitstream(bitstream: bool) -> Self {
        if bitstream {
            Self::BITSTREAM
        } else {
            Self::PCM
        }
    }
}

/// What a reference led to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Decision {
    /// Not measured: the output was not playing, or no timed frame had reached the device or the ring.
    Unmeasured,
    /// Within the threshold (or measure only): nothing more to do.
    Keep,
    /// Beyond [`SyncConfig::range_hns`]: ignored.
    OutOfRange,
    /// Audio ahead: this much more silence goes before the next queued frame.
    Pad(u64),
    /// Audio behind: this many more queued frames are discarded.
    Drop(u64),
}

/// The controller's counters, as `BaleAudioOutStats` reports them.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SyncCounters {
    pub measured: u32,
    pub unmeasured: u32,
    /// The last measured error at the device position, audio pts minus video position (positive = ahead).
    pub last_error_hns: i64,
    pub drop_corrections: u32,
    pub pad_corrections: u32,
    pub dropped_frames: u64,
    pub padded_frames: u64,
}

pub struct SyncController {
    config: SyncConfig,
    rate: u32,
    /// Correction frames not carried out yet: positive = drop, negative = pad.
    pending: i64,
    pub counters: SyncCounters,
}

/// `frames` (signed) at `rate` in 100 ns.
fn signed_hns(frames: i64, rate: u32) -> i64 {
    let hns = hns_from_frames(frames.unsigned_abs(), rate);
    if frames < 0 { -hns } else { hns }
}

impl SyncController {
    pub fn new(config: SyncConfig, rate: u32) -> Self {
        Self {
            config,
            rate,
            pending: 0,
            counters: SyncCounters::default(),
        }
    }

    /// Correction frames still to carry out: (pad, drop); at most one is nonzero.
    pub fn pending(&self) -> (u64, u64) {
        if self.pending < 0 {
            (self.pending.unsigned_abs(), 0)
        } else {
            (0, self.pending.unsigned_abs())
        }
    }

    /// A flush: pending corrections end (the counters stay).
    pub fn reset(&mut self) {
        self.pending = 0;
    }

    pub fn unmeasured(&mut self) -> Decision {
        self.counters.unmeasured = self.counters.unmeasured.saturating_add(1);
        Decision::Unmeasured
    }

    /// One reference: `measured` is the error at the device position (reported), `predicted` the error the
    /// next frame to be handed to the device will have when it is heard (decides). Pending corrections still
    /// move that frame, so they count in: when they already cover the error, nothing more is scheduled.
    pub fn measure(&mut self, measured: i64, predicted: i64) -> Decision {
        self.counters.measured = self.counters.measured.saturating_add(1);
        self.counters.last_error_hns = measured;
        // A pending drop makes the next frame later in the content (audio further ahead), a pad earlier.
        let error = predicted.saturating_add(signed_hns(self.pending, self.rate));
        let magnitude = error.unsigned_abs();
        if magnitude <= self.config.threshold_hns.unsigned_abs() {
            return Decision::Keep;
        }
        if magnitude > self.config.range_hns.unsigned_abs() {
            return Decision::OutOfRange;
        }
        if self.config.granularity == 0 {
            return Decision::Keep;
        }
        let step = magnitude.min(self.config.max_step_hns.unsigned_abs()) as i64;
        let granule = u64::from(self.config.granularity);
        let frames = frames_from_hns(step, self.rate).unsigned_abs();
        let frames = ((frames + granule / 2) / granule * granule).max(granule);
        if error > 0 {
            self.pending = self.pending.saturating_sub_unsigned(frames);
            self.counters.pad_corrections = self.counters.pad_corrections.saturating_add(1);
            Decision::Pad(frames)
        } else {
            self.pending = self.pending.saturating_add_unsigned(frames);
            self.counters.drop_corrections = self.counters.drop_corrections.saturating_add(1);
            Decision::Drop(frames)
        }
    }

    /// Books what a period carried out of the pending corrections.
    pub fn applied(&mut self, plan: &FillPlan) {
        self.pending = self
            .pending
            .saturating_sub_unsigned(plan.dropped)
            .saturating_add(i64::from(plan.pad));
        self.counters.dropped_frames += plan.dropped;
        self.counters.padded_frames += u64::from(plan.pad);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn conversions_round_to_nearest_and_never_overflow() {
        assert_eq!(hns_from_frames(48_000, 48_000), HNS_PER_SECOND);
        assert_eq!(hns_from_frames(1, 48_000), 208);
        assert_eq!(hns_from_frames(1, 44_100), 227);
        assert_eq!(hns_from_frames(u64::MAX, 1), i64::MAX);
        assert_eq!(frames_from_hns(HNS_PER_SECOND, 48_000), 48_000);
        assert_eq!(frames_from_hns(-200_000, 48_000), -960);
        assert_eq!(frames_from_hns(104, 48_000), 0);
        assert_eq!(frames_from_hns(105, 48_000), 1);
        assert_eq!(frames_from_hns(-105, 48_000), -1);
        assert_eq!(frames_from_hns(i64::MIN, u32::MAX), -i64::MAX);
        // IAudioClock frequencies: the byte rate (8 ch 32-bit 48 kHz: 1536000) or 10 MHz.
        assert_eq!(position_frames(1_536_000, 1_536_000, 48_000), 48_000);
        assert_eq!(position_frames(31, 32, 1), 0);
        assert_eq!(position_frames(5, 0, 48_000), 0);
        assert_eq!(position_hns(768_000, 1_536_000), 5_000_000);
        assert_eq!(position_hns(10_000_000, 10_000_000), HNS_PER_SECOND);
        assert_eq!(position_hns(1, 0), 0);
        // A typical 10 MHz QPC is already in 100 ns; other frequencies scale.
        assert_eq!(qpc_hns(123_456_789, 10_000_000), 123_456_789);
        assert_eq!(qpc_hns(3_000_000, 3_000_000), HNS_PER_SECOND);
        assert_eq!(qpc_hns(i64::MAX, 1), i64::MAX);
        assert_eq!(qpc_hns(5, 0), 0);
    }

    #[test]
    fn the_device_position_extrapolates_to_the_reference_time() {
        let sample = Sample {
            frames: 48_000,
            hns: HNS_PER_SECOND,
            qpc_hns: 50_000_000,
        };
        assert_eq!(frames_at(&sample, 50_000_000, 48_000), 48_000);
        assert_eq!(frames_at(&sample, 50_100_000, 48_000), 48_480);
        // A reference taken before the reading looks back.
        assert_eq!(frames_at(&sample, 49_900_000, 48_000), 47_520);
        assert_eq!(frames_at(&sample, 0, 48_000), 0);
    }

    #[test]
    fn a_period_drops_then_pads_then_plays_then_fills_silence() {
        // Plenty queued: the whole period plays.
        assert_eq!(
            FillPlan::new(480, 10_000, 0, 0),
            FillPlan {
                dropped: 0,
                pad: 0,
                data: 480,
                underrun: 0
            }
        );
        // An underrun: 100 frames, then silence (counted as one underrun period by the caller).
        let starved = FillPlan::new(480, 100, 0, 0);
        assert_eq!((starved.data, starved.underrun), (100, 380));
        assert!(!starved.is_silent());
        assert!(FillPlan::new(480, 0, 0, 0).is_silent());
        // A pad larger than a period takes the whole period and continues in the next.
        assert_eq!(
            FillPlan::new(480, 10_000, 1_000, 0),
            FillPlan {
                dropped: 0,
                pad: 480,
                data: 0,
                underrun: 0
            }
        );
        assert_eq!(
            FillPlan::new(480, 10_000, 200, 0),
            FillPlan {
                dropped: 0,
                pad: 200,
                data: 280,
                underrun: 0
            }
        );
        // A drop takes what is queued first; a starved drop leaves silence and continues later.
        assert_eq!(
            FillPlan::new(480, 1_000, 0, 600),
            FillPlan {
                dropped: 600,
                pad: 0,
                data: 400,
                underrun: 80
            }
        );
        assert_eq!(
            FillPlan::new(480, 300, 0, 600),
            FillPlan {
                dropped: 300,
                pad: 0,
                data: 0,
                underrun: 480
            }
        );
    }

    #[test]
    fn small_errors_are_kept_and_large_ones_corrected_once() {
        let mut sync = SyncController::new(SyncConfig::PCM, 48_000);
        assert_eq!(sync.measure(0, 200_000), Decision::Keep, "20 ms is kept");
        assert_eq!(sync.measure(0, -200_000), Decision::Keep);
        // 50 ms ahead: 2400 frames of silence.
        assert_eq!(sync.measure(500_000, 500_000), Decision::Pad(2_400));
        assert_eq!(sync.pending(), (2_400, 0));
        // The same reading again: the pending pad already covers it.
        assert_eq!(sync.measure(500_000, 500_000), Decision::Keep);
        // A period carried out 480 frames: the next frame is now 10 ms nearer the video, 1920 still to come.
        sync.applied(&FillPlan::new(480, 10_000, 2_400, 0));
        assert_eq!(sync.pending(), (1_920, 0));
        assert_eq!(sync.measure(500_000, 400_000), Decision::Keep);
        // The video jumps 80 ms ahead of that: the pending pad turns into a drop.
        assert_eq!(sync.measure(0, -400_000), Decision::Drop(3_840));
        assert_eq!(sync.pending(), (0, 1_920));
        let drop = FillPlan::new(480, 10_000, 0, 1_920);
        assert_eq!((drop.dropped, drop.data), (1_920, 480));
        sync.applied(&drop);
        assert_eq!(sync.pending(), (0, 0));
        assert_eq!(sync.measure(-10_000, 5_000), Decision::Keep);
        assert_eq!(
            sync.counters,
            SyncCounters {
                measured: 7,
                unmeasured: 0,
                last_error_hns: -10_000,
                drop_corrections: 1,
                pad_corrections: 1,
                dropped_frames: 1_920,
                padded_frames: 480,
            }
        );
    }

    #[test]
    fn corrections_are_capped_out_of_range_errors_ignored_and_bitstreams_only_measured() {
        let mut sync = SyncController::new(SyncConfig::PCM, 48_000);
        // 4 s behind: one second per reference until the pending drops cover it.
        for _ in 0..4 {
            assert_eq!(sync.measure(0, -40_000_000), Decision::Drop(48_000));
        }
        assert_eq!(sync.measure(0, -40_000_000), Decision::Keep);
        assert_eq!(sync.pending(), (0, 192_000));
        sync.reset();
        assert_eq!(sync.pending(), (0, 0));
        assert_eq!(
            sync.measure(0, 10 * HNS_PER_SECOND),
            Decision::Pad(48_000),
            "10 s is still in range"
        );
        sync.reset();
        assert_eq!(
            sync.measure(0, 10 * HNS_PER_SECOND + 1),
            Decision::OutOfRange
        );
        assert_eq!(sync.measure(0, i64::MIN), Decision::OutOfRange);
        assert_eq!(sync.unmeasured(), Decision::Unmeasured);
        assert_eq!((sync.counters.measured, sync.counters.unmeasured), (8, 1));
        let mut bitstream = SyncController::new(SyncConfig::for_bitstream(true), 192_000);
        assert_eq!(bitstream.measure(5_000_000, 5_000_000), Decision::Keep);
        assert_eq!(bitstream.counters.last_error_hns, 5_000_000);
        assert_eq!(bitstream.pending(), (0, 0));
        assert_eq!(SyncConfig::for_bitstream(false), SyncConfig::PCM);
        // Granules round to the nearest whole multiple, at least one.
        let mut bursts = SyncController::new(
            SyncConfig {
                granularity: 1_536,
                ..SyncConfig::PCM
            },
            48_000,
        );
        assert_eq!(bursts.measure(0, 210_000), Decision::Pad(1_536));
        bursts.reset();
        assert_eq!(bursts.measure(0, -500_000), Decision::Drop(3_072));
    }
}
