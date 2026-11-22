//! A bounded, allocation-free feed of playback transitions. The diagnostic log's first 400
//! events is not a playback counter: long streams must keep reporting stalls after it fills.

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct PlaybackEvent {
    pub seconds: f64,
    pub kind: u32,
    pub reserved: u32,
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
    pub fn observe(&mut self, kind: u32, seconds: f64) {
        // MF_MEDIA_ENGINE_EVENT: play, pause, waiting, playing, seeking, seeked,
        // ended, bufferingstarted/ended. In particular, no per-frame timeupdate/progress.
        if !matches!(kind, 8 | 9 | 12 | 13 | 16 | 17 | 19 | 1005 | 1006) {
            return;
        }
        if self.len == self.events.len() {
            self.head = (self.head + 1) % self.events.len();
            self.len -= 1;
            self.lost = self.lost.saturating_add(1);
        }
        self.events[(self.head + self.len) % self.events.len()] = PlaybackEvent {
            seconds,
            kind,
            reserved: 0,
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

    #[test]
    fn ignores_frame_events_and_retains_timestamps_in_order_across_partial_drains() {
        let mut events = PlaybackEvents::default();
        for _ in 0..1000 {
            events.observe(18, 1.0);
        }
        events.observe(12, 1.2);
        events.observe(1005, 1.21);
        events.observe(1006, 1.5);
        let mut out = [PlaybackEvent::default(); 2];
        assert_eq!(events.drain(&mut out), 2);
        assert_eq!((out[0].kind, out[0].seconds), (12, 1.2));
        assert_eq!((out[1].kind, out[1].seconds), (1005, 1.21));
        assert_eq!(events.drain(&mut out), 1);
        assert_eq!((out[0].kind, out[0].seconds), (1006, 1.5));
        assert_eq!(events.lost, 0);
    }

    #[test]
    fn long_playback_keeps_reporting_and_overflow_is_visible() {
        let mut events = PlaybackEvents::default();
        let mut out = [PlaybackEvent::default(); 8];
        for i in 0..1000 {
            events.observe(12, f64::from(i));
            assert_eq!(events.drain(&mut out), 1);
        }
        for i in 0..300 {
            events.observe(13, f64::from(i));
        }
        assert_eq!(events.lost, 44);
        assert_eq!(events.drain(&mut out), 8);
        assert_eq!(out[0].seconds, 44.0);
    }
}
