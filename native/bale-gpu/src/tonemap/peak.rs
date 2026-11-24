//! The source peak the latest stage applies (2026-11-24), for Bale's once-per-stream log line and
//! stats row: one small record under a mutex. Apply takes it only when its stage's peak or peak
//! source changes (normally once per stream); `bale_tonemap_peak_read` only tries it (S_FALSE when
//! busy), so it never waits, and neither side touches D3D or the wrapper's source lock.
use super::types::BaleToneMapPeakInfo;
#[cfg(any(windows, test))]
use super::{
    cpu::{self, PeakSource},
    types::{BaleToneMapConfig, BaleToneMapFrameInfo},
};
use std::sync::{Mutex, TryLockError};

/// `BaleToneMapPeakInfo.flags` bit 0: the stage runs the debug `fixedpeak` switch.
#[cfg(any(windows, test))]
pub const FLAG_FIXED_PEAK: u32 = 1;

const EMPTY: BaleToneMapPeakInfo = BaleToneMapPeakInfo {
    stage: 0,
    changes: 0,
    source: 0,
    peak_nits: 0.0,
    config_peak_nits: 0.0,
    max_cll: 0,
    mastering_max_nits: 0.0,
    transfer: 0,
    flags: 0,
};
static REPORT: Mutex<BaleToneMapPeakInfo> = Mutex::new(EMPTY);

/// A new stage owns the report from now on: its number (1, 2, ...), and no frame applied yet.
#[cfg(any(windows, test))]
fn begin(report: &mut BaleToneMapPeakInfo, config: &BaleToneMapConfig, fixed: bool) -> u64 {
    let stage = report.stage.wrapping_add(1).max(1);
    *report = BaleToneMapPeakInfo {
        stage,
        config_peak_nits: config.source_peak_nits,
        flags: if fixed { FLAG_FIXED_PEAK } else { 0 },
        ..EMPTY
    };
    stage
}

/// Stage `stage` now tone-maps `frame` from `peak` (`fixed`: under `fixedpeak`). Ignored once a newer
/// stage owns the report (it always describes the most recently created stage, as the stage timing
/// snapshot does).
#[cfg(any(windows, test))]
fn record(
    report: &mut BaleToneMapPeakInfo,
    stage: u64,
    frame: &BaleToneMapFrameInfo,
    (peak_nits, source): (f32, PeakSource),
    fixed: bool,
) {
    if report.stage != stage {
        return;
    }
    let (cll, mastering) = cpu::metadata_peaks(frame);
    *report = BaleToneMapPeakInfo {
        changes: report.changes.saturating_add(1),
        source: source as u32,
        peak_nits,
        max_cll: cll.map_or(0, |_| frame.max_cll),
        mastering_max_nits: mastering.unwrap_or(0.0),
        transfer: u32::from(frame.transfer),
        flags: if fixed { FLAG_FIXED_PEAK } else { 0 },
        ..*report
    };
}

#[cfg(windows)]
fn lock() -> std::sync::MutexGuard<'static, BaleToneMapPeakInfo> {
    REPORT
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// Called once per successful create, after its self-test.
#[cfg(windows)]
pub(super) fn start(config: &BaleToneMapConfig, fixed: bool) -> u64 {
    begin(&mut lock(), config, fixed)
}

/// Called by apply when its stage's peak or peak source changed.
#[cfg(windows)]
pub(super) fn publish(
    stage: u64,
    frame: &BaleToneMapFrameInfo,
    peak: (f32, PeakSource),
    fixed: bool,
) {
    record(&mut lock(), stage, frame, peak, fixed);
}

/// A copy of the report, or None while apply holds it (the caller reads again later).
pub(super) fn snapshot() -> Option<BaleToneMapPeakInfo> {
    match REPORT.try_lock() {
        Ok(report) => Some(*report),
        Err(TryLockError::Poisoned(report)) => Some(*report.into_inner()),
        Err(TryLockError::WouldBlock) => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(source_peak_nits: f32) -> BaleToneMapConfig {
        BaleToneMapConfig {
            width: 16,
            height: 8,
            curve: 1,
            source_peak_nits,
            target_white_nits: 100.0,
            ..Default::default()
        }
    }

    fn pq(max_cll: Option<u32>, mastering: Option<f32>) -> BaleToneMapFrameInfo {
        BaleToneMapFrameInfo {
            transfer: 16,
            primaries: 9,
            matrix: 9,
            has_content_light: u8::from(max_cll.is_some()),
            max_cll: max_cll.unwrap_or(0),
            has_mastering: u8::from(mastering.is_some()),
            mastering_max_nits: mastering.unwrap_or(0.0),
            ..Default::default()
        }
    }

    /// Each create starts a fresh report; each change counts once; an older stage cannot overwrite a
    /// newer one's report. Local records only: the process-wide report stays untouched by unit tests.
    #[test]
    fn report_follows_the_newest_stage_and_counts_changes() {
        let mut report = EMPTY;
        let first = begin(&mut report, &config(1000.0), false);
        assert_eq!(first, 1);
        assert_eq!((report.stage, report.changes, report.source), (1, 0, 0));
        assert_eq!(report.config_peak_nits, 1000.0);
        let frame = pq(Some(1500), Some(4000.0));
        let c = config(1000.0);
        record(
            &mut report,
            first,
            &frame,
            cpu::source_peak(&c, &frame, false),
            false,
        );
        assert_eq!(report.changes, 1);
        assert_eq!(report.source, PeakSource::MaxCll as u32);
        assert_eq!(report.peak_nits, 1500.0);
        assert_eq!((report.max_cll, report.mastering_max_nits), (1500, 4000.0));
        assert_eq!((report.transfer, report.flags), (16, 0));
        let second = begin(&mut report, &config(0.0), true);
        assert_eq!(second, 2);
        assert_eq!((report.changes, report.flags), (0, FLAG_FIXED_PEAK));
        assert_eq!(report.config_peak_nits, 0.0);
        // The first stage's late change belongs to a replaced report.
        record(
            &mut report,
            first,
            &frame,
            (4000.0, PeakSource::Mastering),
            false,
        );
        assert_eq!(report.changes, 0);
        let none = pq(None, Some(f32::NAN));
        record(
            &mut report,
            second,
            &none,
            cpu::source_peak(&c, &none, true),
            true,
        );
        assert_eq!(report.changes, 1);
        assert_eq!(report.source, PeakSource::Config as u32);
        assert_eq!((report.peak_nits, report.flags), (1000.0, FLAG_FIXED_PEAK));
        assert_eq!((report.max_cll, report.mastering_max_nits), (0, 0.0));
        // Stage numbers never return to 0, which means "no stage yet".
        report.stage = u64::MAX;
        assert_eq!(begin(&mut report, &c, false), 1);
    }

    #[test]
    fn snapshot_reads_without_waiting() {
        assert!(snapshot().is_some());
        let _held = REPORT.lock().unwrap();
        assert!(snapshot().is_none());
    }
}
