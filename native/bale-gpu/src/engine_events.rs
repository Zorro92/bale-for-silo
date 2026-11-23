//! A bounded, allocation-free feed of playback transitions. The diagnostic log's first 400
//! events is not a playback counter: long streams must keep reporting stalls after it fills.

/// One playback transition, as `bale_gpu_engine_session_events` hands it out (C layout).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PlaybackEvent {
    /// Seconds on the session's event clock (the drain also returns that clock's now).
    pub seconds: f64,
    /// MF_MEDIA_ENGINE_EVENT.
    pub kind: u32,
    pub reserved: u32,
    /// QueryPerformanceCounter milliseconds at the event (counter × 1000 / frequency): the clock of the
    /// frame observer's records and of the FFmpegInteropX fork's per-frame log lines (ABI 16).
    pub qpc_ms: f64,
    /// Engine position in seconds at the event, read only while the session's debug frame log is on
    /// (`bale_gpu_engine_session_frame_log`); NaN otherwise (ABI 16).
    pub position: f64,
}

pub struct PlaybackEvents {
    events: [PlaybackEvent; 256],
    head: usize,
    len: usize,
    pub lost: u32,
}

impl Default for PlaybackEvents {
    fn default() -> Self {
        Self {
            events: [PlaybackEvent::default(); 256],
            head: 0,
            len: 0,
            lost: 0,
        }
    }
}

impl PlaybackEvents {
    pub fn observe(&mut self, event: PlaybackEvent) {
        // MF_MEDIA_ENGINE_EVENT: play, pause, waiting, playing, seeking, seeked,
        // ended, bufferingstarted/ended. In particular, no per-frame timeupdate/progress.
        if !matches!(event.kind, 8 | 9 | 12 | 13 | 16 | 17 | 19 | 1005 | 1006) {
            return;
        }
        if self.len == self.events.len() {
            self.head = (self.head + 1) % self.events.len();
            self.len -= 1;
            self.lost = self.lost.saturating_add(1);
        }
        self.events[(self.head + self.len) % self.events.len()] = PlaybackEvent {
            reserved: 0,
            ..event
        };
        self.len += 1;
    }

    pub fn drain(&mut self, out: &mut [PlaybackEvent]) -> usize {
        let count = out.len().min(self.len);
        for value in &mut out[..count] {
            *value = self.events[self.head];
            self.head = (self.head + 1) % self.events.len();
        }
        self.len -= count;
        count
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(kind: u32, seconds: f64) -> PlaybackEvent {
        PlaybackEvent {
            seconds,
            kind,
            qpc_ms: 1000.0 + seconds * 1000.0,
            position: f64::NAN,
            ..PlaybackEvent::default()
        }
    }

    #[test]
    fn layout_matches_the_csharp_abi() {
        assert_eq!(core::mem::size_of::<PlaybackEvent>(), 32);
        assert_eq!(core::mem::offset_of!(PlaybackEvent, seconds), 0);
        assert_eq!(core::mem::offset_of!(PlaybackEvent, kind), 8);
        assert_eq!(core::mem::offset_of!(PlaybackEvent, reserved), 12);
        assert_eq!(core::mem::offset_of!(PlaybackEvent, qpc_ms), 16);
        assert_eq!(core::mem::offset_of!(PlaybackEvent, position), 24);
    }

    #[test]
    fn ignores_frame_events_and_retains_timestamps_in_order_across_partial_drains() {
        let mut events = PlaybackEvents::default();
        for _ in 0..1000 {
            events.observe(event(18, 1.0));
        }
        events.observe(event(12, 1.2));
        events.observe(event(1005, 1.21));
        events.observe(event(1006, 1.5));
        let mut out = [PlaybackEvent::default(); 2];
        assert_eq!(events.drain(&mut out), 2);
        assert_eq!((out[0].kind, out[0].seconds), (12, 1.2));
        assert_eq!((out[1].kind, out[1].seconds), (1005, 1.21));
        assert_eq!(events.drain(&mut out), 1);
        assert_eq!((out[0].kind, out[0].seconds), (1006, 1.5));
        assert_eq!(events.lost, 0);
    }

    #[test]
    fn keeps_the_qpc_time_and_position_of_each_event() {
        let mut events = PlaybackEvents::default();
        events.observe(PlaybackEvent {
            seconds: 2.5,
            kind: 12,
            reserved: 7,
            qpc_ms: 123_456.789,
            position: 41.708,
        });
        events.observe(event(13, 2.75));
        let mut out = [PlaybackEvent::default(); 4];
        assert_eq!(events.drain(&mut out), 2);
        assert_eq!(
            out[0],
            PlaybackEvent {
                seconds: 2.5,
                kind: 12,
                reserved: 0,
                qpc_ms: 123_456.789,
                position: 41.708,
            }
        );
        assert_eq!((out[1].kind, out[1].qpc_ms), (13, 3750.0));
        assert!(out[1].position.is_nan());
    }

    #[test]
    fn long_playback_keeps_reporting_and_overflow_is_visible() {
        let mut events = PlaybackEvents::default();
        let mut out = [PlaybackEvent::default(); 8];
        for i in 0..1000 {
            events.observe(event(12, f64::from(i)));
            assert_eq!(events.drain(&mut out), 1);
        }
        for i in 0..300 {
            events.observe(event(13, f64::from(i)));
        }
        assert_eq!(events.lost, 44);
        assert_eq!(events.drain(&mut out), 8);
        assert_eq!(out[0].seconds, 44.0);
        assert_eq!(out[0].qpc_ms, 45_000.0);
    }
}
