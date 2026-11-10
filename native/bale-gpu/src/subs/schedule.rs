//! When the subtitle picture can change on its own. Most lines are static: between an event's
//! start and end libass returns the same images, so the render thread sleeps until the next
//! boundary instead of asking libass every frame. Only events whose look depends on the time
//! (fades, moves, transforms, karaoke, scrolling effects) need a render per frame.

use std::time::Duration;

use super::Clock;

/// One event of the track, as libass will show it: active for `start_ms <= t < start_ms + duration_ms`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Event {
    pub start_ms: i64,
    pub duration_ms: i64,
    /// Its images change while it is shown (see [`is_animated`]).
    pub animated: bool,
}

/// Whether an event with this `Text` and `Effect` changes while it is shown.
///
/// Override tags are matched as libass does (`parse_tags` in ass_parse.c): after the backslash
/// and optional blanks, case-sensitively and by prefix, so `\kf`, `\ko`, `\kt` and anything else
/// starting with `k`, `K` or `t` counts. A false positive only costs renders; a false negative
/// would freeze an animation, so text outside override blocks is searched too.
pub fn is_animated(text: &[u8], effect: &[u8]) -> bool {
    // libass knows Banner, Scroll up and Scroll down; anything else non-empty is treated alike.
    if !effect.is_empty() {
        return true;
    }
    let mut rest = text;
    while let Some(at) = rest.iter().position(|&byte| byte == b'\\') {
        rest = &rest[at + 1..];
        let name = rest.trim_ascii_start();
        if name.starts_with(b"fad")
            || name.starts_with(b"move")
            || matches!(name.first(), Some(b't' | b'k' | b'K'))
        {
            return true;
        }
    }
    false
}

/// What the render thread does after a render at some media time.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Next {
    /// The picture changes continuously: render every display frame.
    EveryFrame,
    /// Nothing changes before `at_ms`, which the clock reaches `wall` after it was reported.
    Boundary { at_ms: i64, wall: Duration },
    /// Nothing changes until the player reports a new clock (paused, stopped, or past the last
    /// event).
    Never,
}

/// The event boundaries of one track, built once at load so no frame scans the events.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Schedule {
    /// Every start and end, ascending, without duplicates.
    boundaries: Vec<i64>,
    /// `[start, end)` spans with at least one animated event, ascending and disjoint.
    animated: Vec<(i64, i64)>,
}

impl Schedule {
    pub fn new(events: impl IntoIterator<Item = Event>) -> Self {
        let mut boundaries = Vec::new();
        let mut spans = Vec::new();
        for event in events {
            // libass never shows an event without a positive duration.
            if event.duration_ms <= 0 {
                continue;
            }
            let end = event.start_ms.saturating_add(event.duration_ms);
            boundaries.extend([event.start_ms, end]);
            if event.animated {
                spans.push((event.start_ms, end));
            }
        }
        boundaries.sort_unstable();
        boundaries.dedup();
        spans.sort_unstable();
        let mut animated: Vec<(i64, i64)> = Vec::new();
        for (start, end) in spans {
            match animated.last_mut() {
                Some(last) if start <= last.1 => last.1 = last.1.max(end),
                _ => animated.push((start, end)),
            }
        }
        Schedule {
            boundaries,
            animated,
        }
    }

    /// The first event start or end after `t_ms`; `None` past the last one.
    pub fn next_boundary(&self, t_ms: i64) -> Option<i64> {
        let at = self
            .boundaries
            .partition_point(|&boundary| boundary <= t_ms);
        self.boundaries.get(at).copied()
    }

    /// Whether an animated event is shown at `t_ms`.
    pub fn animated_at(&self, t_ms: i64) -> bool {
        let at = self.animated.partition_point(|&(_, end)| end <= t_ms);
        self.animated
            .get(at)
            .is_some_and(|&(start, _)| start <= t_ms)
    }

    /// What to do after rendering at `now_ms` with the player's `clock`.
    pub fn next(&self, now_ms: i64, clock: &Clock) -> Next {
        self.next_with(now_ms, clock, None)
    }

    /// [`Schedule::next`] with one more boundary after `now_ms` that is not in the track: the
    /// next change of the image subtitles drawn under it.
    pub fn next_with(&self, now_ms: i64, clock: &Clock, other_ms: Option<i64>) -> Next {
        if !clock.playing || clock.rate == 0.0 {
            return Next::Never;
        }
        // Playing backwards is not worth a schedule of its own.
        if clock.rate < 0.0 || self.animated_at(now_ms) {
            return Next::EveryFrame;
        }
        let at_ms = match (self.next_boundary(now_ms), other_ms) {
            (Some(own), Some(other)) => own.min(other),
            (own, other) => match own.or(other) {
                Some(at_ms) => at_ms,
                None => return Next::Never,
            },
        };
        let seconds = (at_ms as f64 - clock.position_ms as f64) / 1000.0 / clock.rate;
        let wall = Duration::try_from_secs_f64(seconds.max(0.0)).unwrap_or(Duration::MAX);
        Next::Boundary { at_ms, wall }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(start_ms: i64, duration_ms: i64, animated: bool) -> Event {
        Event {
            start_ms,
            duration_ms,
            animated,
        }
    }

    fn playing(position_ms: i64, rate: f64) -> Clock {
        Clock {
            position_ms,
            rate,
            playing: true,
        }
    }

    #[test]
    fn animation_tags_are_detected() {
        for text in [
            r"{\fad(200,200)}Hello",
            r"{\fade(255,0,255,0,200,800,1000)}Hello",
            r"{\move(0,0,100,100)}Hello",
            r"{\pos(10,10)\t(\fs40)}Hello",
            r"{\t(0,500,\frz360)}Hello",
            r"{\k20}Ka{\k30}ra",
            r"{\K20}Ka",
            r"{\kf20}Ka",
            r"{\ko20}Ka",
            r"{\kt100}Ka",
            // libass skips blanks after the backslash and before the parenthesis.
            r"{\ fad(200,200)}Hello",
            r"{\t (\fs40)}Hello",
            r"plain {\b1}then{\an8\move(1,2,3,4)} later",
            // Prefix matching, as libass: these parse as \k and \t there too.
            r"{\kerning1}Hello",
            r"{\tx}Hello",
        ] {
            assert!(is_animated(text.as_bytes(), b""), "{text}");
        }
    }

    #[test]
    fn static_text_is_not_animated() {
        for text in [
            "",
            "Hello",
            r"Two\Nlines\hhere\n",
            r"{\an8\pos(960,100)\b1\i1\fs48\fnArial\c&HFFFFFF&\bord3\shad1}Top",
            r"{\fax0.2\fay0.1\frz30\fscx120\blur2\be1\clip(0,0,100,100)}Skew",
            // Case matters: libass does not know these.
            r"{\Fad(200,200)}Hello",
            r"{\MOVE(0,0,1,1)}Hello",
            r"{\T(\fs40)}Hello",
            r"{\p1}m 0 0 l 10 0 10 10{\p0}",
            "trailing backslash \\",
        ] {
            assert!(!is_animated(text.as_bytes(), b""), "{text}");
        }
    }

    #[test]
    fn any_effect_is_animated() {
        assert!(is_animated(b"Hello", b"Banner;30"));
        assert!(is_animated(b"Hello", b"Scroll up;0;100;20"));
        assert!(is_animated(b"Hello", b"Scroll down;0;100;20"));
        assert!(is_animated(b"Hello", b"fx"));
        assert!(!is_animated(b"Hello", b""));
    }

    #[test]
    fn next_boundary_skips_gaps() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, false)]);
        assert_eq!(schedule.next_boundary(i64::MIN), Some(1000));
        assert_eq!(schedule.next_boundary(0), Some(1000));
        assert_eq!(schedule.next_boundary(999), Some(1000));
        assert_eq!(schedule.next_boundary(1500), Some(3000));
        assert_eq!(schedule.next_boundary(3500), Some(5000));
        assert_eq!(schedule.next_boundary(5999), Some(6000));
    }

    #[test]
    fn next_boundary_is_strictly_after_a_boundary() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, false)]);
        assert_eq!(schedule.next_boundary(1000), Some(3000));
        assert_eq!(schedule.next_boundary(3000), Some(5000));
        assert_eq!(schedule.next_boundary(5000), Some(6000));
    }

    #[test]
    fn next_boundary_ends_after_the_last_event() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, false)]);
        assert_eq!(schedule.next_boundary(6000), None);
        assert_eq!(schedule.next_boundary(i64::MAX), None);
        assert_eq!(Schedule::default().next_boundary(0), None);
    }

    #[test]
    fn overlapping_events_keep_every_boundary_once() {
        // Unsorted, overlapping, nested, and two sharing a start and an end.
        let schedule = Schedule::new([
            event(4000, 1000, false),
            event(1000, 3000, false),
            event(2000, 500, false),
            event(2000, 500, false),
            event(3000, 1000, false),
        ]);
        let mut seen = Vec::new();
        let mut t = 0;
        while let Some(next) = schedule.next_boundary(t) {
            seen.push(next);
            t = next;
        }
        assert_eq!(seen, [1000, 2000, 2500, 3000, 4000, 5000]);
    }

    #[test]
    fn events_without_a_duration_are_ignored() {
        let schedule = Schedule::new([
            event(1000, 0, true),
            event(2000, -5, true),
            event(i64::MAX - 10, i64::MAX, false),
        ]);
        assert!(!schedule.animated_at(1000));
        assert!(!schedule.animated_at(2000));
        assert_eq!(schedule.next_boundary(0), Some(i64::MAX - 10));
        assert_eq!(schedule.next_boundary(i64::MAX - 10), Some(i64::MAX));
    }

    #[test]
    fn animated_spans_are_half_open_and_merged() {
        let schedule = Schedule::new([
            event(0, 10_000, false),
            event(1000, 1000, true),
            event(1500, 1000, true),
            event(2500, 500, true),
            event(1200, 100, true),
            event(6000, 1000, true),
        ]);
        assert!(!schedule.animated_at(999));
        assert!(schedule.animated_at(1000));
        assert!(schedule.animated_at(2000));
        assert!(schedule.animated_at(2500));
        assert!(schedule.animated_at(2999));
        assert!(!schedule.animated_at(3000));
        assert!(!schedule.animated_at(5999));
        assert!(schedule.animated_at(6000));
        assert!(!schedule.animated_at(7000));
        assert!(!Schedule::default().animated_at(0));
    }

    #[test]
    fn many_events_answer_from_the_sorted_lists() {
        // 6000 lines of 2 s every 3 s; every 100th fades.
        let schedule = Schedule::new((0..6000).map(|i| event(i * 3000, 2000, i % 100 == 0)));
        assert_eq!(schedule.next_boundary(2_999_999), Some(3_000_000));
        assert_eq!(schedule.next_boundary(3_000_000), Some(3_002_000));
        assert!(schedule.animated_at(300_500));
        assert!(!schedule.animated_at(303_500));
        assert_eq!(schedule.next_boundary(17_999_000), None);
    }

    #[test]
    fn next_sleeps_to_the_boundary_in_wall_time() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, true)]);
        let clock = playing(400, 1.0);
        // 100 ms after the report the media time is 500: the line starts 600 ms after the report.
        assert_eq!(
            schedule.next(500, &clock),
            Next::Boundary {
                at_ms: 1000,
                wall: Duration::from_millis(600)
            }
        );
        assert_eq!(
            schedule.next(1000, &playing(400, 2.0)),
            Next::Boundary {
                at_ms: 3000,
                wall: Duration::from_millis(1300)
            }
        );
        assert_eq!(
            schedule.next(3000, &playing(3000, 0.5)),
            Next::Boundary {
                at_ms: 5000,
                wall: Duration::from_millis(4000)
            }
        );
        // A boundary the clock has already passed is due at once.
        assert_eq!(
            schedule.next(500, &playing(1200, 1.0)),
            Next::Boundary {
                at_ms: 1000,
                wall: Duration::ZERO
            }
        );
    }

    #[test]
    fn next_renders_every_frame_only_while_animated() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, true)]);
        let clock = playing(0, 1.0);
        assert!(matches!(
            schedule.next(4999, &clock),
            Next::Boundary { at_ms: 5000, .. }
        ));
        assert_eq!(schedule.next(5000, &clock), Next::EveryFrame);
        assert_eq!(schedule.next(5999, &clock), Next::EveryFrame);
        assert_eq!(schedule.next(6000, &clock), Next::Never);
        // Backwards playback has no schedule.
        assert_eq!(schedule.next(2000, &playing(2000, -1.0)), Next::EveryFrame);
    }

    #[test]
    fn next_with_takes_the_earlier_of_both_boundaries() {
        let schedule = Schedule::new([event(5000, 1000, false)]);
        let clock = playing(1000, 1.0);
        let at = |other| match schedule.next_with(1000, &clock, other) {
            Next::Boundary { at_ms, .. } => Some(at_ms),
            _ => None,
        };
        assert_eq!(at(None), Some(5000));
        assert_eq!(at(Some(3000)), Some(3000));
        assert_eq!(at(Some(9000)), Some(5000));
        // Without a track the other boundary alone paces the thread.
        assert_eq!(
            Schedule::default().next_with(1000, &clock, Some(1500)),
            Next::Boundary {
                at_ms: 1500,
                wall: Duration::from_millis(500),
            }
        );
        assert_eq!(
            Schedule::default().next_with(1000, &clock, None),
            Next::Never
        );
    }

    #[test]
    fn next_never_wakes_when_time_stands_still() {
        let schedule = Schedule::new([event(1000, 2000, false), event(5000, 1000, true)]);
        let paused = Clock {
            position_ms: 5500,
            rate: 1.0,
            playing: false,
        };
        assert_eq!(schedule.next(5500, &paused), Next::Never);
        assert_eq!(schedule.next(500, &playing(500, 0.0)), Next::Never);
        assert_eq!(schedule.next(500, &playing(500, -0.0)), Next::Never);
        assert_eq!(Schedule::default().next(0, &playing(0, 1.0)), Next::Never);
    }

    #[test]
    fn next_survives_extreme_clocks() {
        let schedule = Schedule::new([event(i64::MAX - 10, 5, false)]);
        let next = schedule.next(i64::MIN, &playing(i64::MIN, 1e-300));
        assert_eq!(
            next,
            Next::Boundary {
                at_ms: i64::MAX - 10,
                wall: Duration::MAX
            }
        );
    }
}
