//! The decoder end to end: packets in, cues and live events out.

use std::collections::BTreeMap;

use super::extract::tests::{
    annex_b, cc_data_bytes, ga94_payload, h264_sei, length_prefixed, sei_message,
};
use super::*;

/// 30 fps in 100 ns.
const FRAME: i64 = 333_333;

fn parity(b: u8) -> u8 {
    let b = b & 0x7F;
    if b.count_ones().is_multiple_of(2) {
        b | 0x80
    } else {
        b
    }
}

const RCL: [u8; 2] = [0x14, 0x20];
const RU2: [u8; 2] = [0x14, 0x25];
const RDC: [u8; 2] = [0x14, 0x29];
const EDM: [u8; 2] = [0x14, 0x2C];
const CR: [u8; 2] = [0x14, 0x2D];
const ENM: [u8; 2] = [0x14, 0x2E];
const EOC: [u8; 2] = [0x14, 0x2F];
const ROW15: [u8; 2] = [0x14, 0x60];

/// Control codes go out twice, as encoders send them.
fn code(code: [u8; 2]) -> Vec<[u8; 2]> {
    vec![code, code]
}

fn text(text: &str) -> Vec<[u8; 2]> {
    text.as_bytes()
        .chunks(2)
        .map(|pair| [pair[0], pair.get(1).copied().unwrap_or(0)])
        .collect()
}

/// One field-1 pair per frame, each event's pairs from its frame on (the clip generator's
/// schedule).
fn schedule(events: &[(usize, Vec<[u8; 2]>)]) -> BTreeMap<usize, [u8; 2]> {
    let mut slots = BTreeMap::new();
    let mut cursor = 0;
    for (frame, pairs) in events {
        cursor = cursor.max(*frame);
        for &pair in pairs {
            slots.insert(cursor, pair);
            cursor += 1;
        }
    }
    slots
}

/// An H.264 packet: an SEI with the frame's field-1 pair (null without one), a null field-2 pair
/// and DTVCC padding, then a slice.
fn packet(pair: Option<[u8; 2]>, nal_length_size: u8, field: u8) -> Vec<u8> {
    let bytes = pair.map_or([0x80, 0x80], |[a, b]| {
        [parity(a), if b == 0 { 0x80 } else { parity(b) }]
    });
    let pairs = if field == 0 {
        [(false, bytes), (true, [0x80, 0x80])]
    } else {
        [(false, [0x80, 0x80]), (true, bytes)]
    };
    let cc = cc_data_bytes(&pairs, 2);
    let sei = h264_sei(&[sei_message(&ga94_payload(&cc))]);
    let slice = [0x65, 0x88, 0x84, 0x21];
    match nal_length_size {
        0 => annex_b(&[&sei, &slice]),
        size => length_prefixed(&[&sei, &slice], usize::from(size)),
    }
}

/// Pushes frames `order` (decode order) of `slots`, then drains.
fn run(
    captions: &Captions,
    slots: &BTreeMap<usize, [u8; 2]>,
    order: &[usize],
    size: u8,
    field: u8,
) {
    for &frame in order {
        let data = packet(slots.get(&frame).copied(), size, field);
        let pts = Some(frame as i64 * FRAME);
        assert_eq!(captions.push(&data, pts, 1, u32::from(size)), S_OK);
    }
    captions.drain();
}

/// I P B B ... over `frames` frames.
fn b_frame_order(frames: usize) -> Vec<usize> {
    let mut order = vec![0];
    let mut anchor = 0;
    while anchor + 3 < frames {
        order.extend([anchor + 3, anchor + 1, anchor + 2]);
        anchor += 3;
    }
    order.extend(anchor + 1..frames);
    order
}

fn summary(cues: &[Cue]) -> Vec<(i64, Option<i64>, String)> {
    cues.iter()
        .map(|cue| (cue.start_ms, cue.end_ms, cue.text()))
        .collect()
}

/// A live track stand-in: applies the commands and answers what shows when.
#[derive(Default)]
struct Track {
    generation: u64,
    gone: bool,
    events: BTreeMap<i32, (i64, i64, String)>,
    log: Vec<LiveCommand>,
}

#[derive(Default)]
struct Recorder(Mutex<Track>);

impl LiveSink for Recorder {
    fn live(&self, generation: u64, commands: &mut Vec<LiveCommand>) -> i32 {
        let mut track = self.0.lock().unwrap();
        if track.gone || track.generation != generation {
            commands.clear();
            return E_NOT_FOUND;
        }
        for command in commands.drain(..) {
            match &command {
                LiveCommand::Add {
                    id,
                    start_ms,
                    end_ms,
                    text,
                } => {
                    assert!(
                        track
                            .events
                            .insert(*id, (*start_ms, *end_ms, text.clone()))
                            .is_none()
                    );
                }
                LiveCommand::SetEnd { id, end_ms } => {
                    track.events.get_mut(id).expect("known event").1 = *end_ms;
                }
                LiveCommand::Clear => track.events.clear(),
            }
            track.log.push(command);
        }
        S_OK
    }

    fn end_live(&self, generation: u64) {
        let mut track = self.0.lock().unwrap();
        if track.generation == generation {
            track.generation = 0;
            track.events.clear();
        }
    }
}

impl Recorder {
    fn new(generation: u64) -> Arc<Recorder> {
        let recorder = Arc::new(Recorder::default());
        recorder.0.lock().unwrap().generation = generation;
        recorder
    }

    fn sink(self: &Arc<Self>) -> Weak<dyn LiveSink> {
        let sink: Arc<dyn LiveSink> = self.clone();
        Arc::downgrade(&sink)
    }

    /// The event texts showing at `t_ms`.
    fn visible(&self, t_ms: i64) -> Vec<String> {
        let track = self.0.lock().unwrap();
        track
            .events
            .values()
            .filter(|(start, end, _)| *start <= t_ms && t_ms < *end)
            .map(|(_, _, text)| text.clone())
            .collect()
    }

    fn events(&self) -> usize {
        self.0.lock().unwrap().events.len()
    }
}

fn hello_stream() -> BTreeMap<usize, [u8; 2]> {
    schedule(&[
        (
            0,
            [code(RCL), code(ENM), code(ROW15), text("HELLO")].concat(),
        ),
        (30, code(EOC)),
        (90, code(EDM)),
        (100, [code(RCL), code(ROW15), text("BYE")].concat()),
        (120, code(EOC)),
        (150, code(EOC)),
    ])
}

#[test]
fn a_pop_on_caption_runs_from_end_of_caption_to_its_erase_in_every_framing() {
    let slots = hello_stream();
    let expected = vec![
        (1000, Some(3000), "HELLO".to_owned()),
        (4000, Some(5000), "BYE".to_owned()),
    ];
    let in_order: Vec<usize> = (0..180).collect();
    for size in [0u8, 1, 2, 4] {
        for order in [in_order.clone(), b_frame_order(180)] {
            let captions = Captions::new();
            run(&captions, &slots, &order, size, 0);
            assert_eq!(summary(&captions.cues()), expected, "size {size}");
            let stats = captions.stats();
            assert_eq!(stats.packets, 180);
            assert_eq!(stats.caption_packets, 180);
            assert_eq!(stats.cues, 2);
            assert_eq!(stats.late, 0);
            assert_eq!(stats.seen, SEEN_CC1 | SEEN_CC_DATA);
            assert!(stats.pairs > 10);
        }
    }
}

#[test]
fn an_attached_overlay_gets_each_caption_as_it_comes_and_ends_it_later() {
    let slots = hello_stream();
    let captions = Captions::new();
    let recorder = Recorder::new(7);
    assert_eq!(
        captions.attach(recorder.sink(), 7, ConvertOptions::default(), 0),
        S_OK
    );
    for frame in 0..60 {
        let data = packet(slots.get(&frame).copied(), 4, 0);
        captions.push(&data, Some(frame as i64 * FRAME), 1, 4);
    }
    // The caption is up from 1 s and open-ended (its erase has not arrived).
    assert_eq!(recorder.visible(999), Vec::<String>::new());
    assert_eq!(recorder.visible(1000), ["{\\an1\\pos(192,972)}HELLO"]);
    assert_eq!(recorder.visible(20_000), ["{\\an1\\pos(192,972)}HELLO"]);
    for frame in 60..180 {
        let data = packet(slots.get(&frame).copied(), 4, 0);
        captions.push(&data, Some(frame as i64 * FRAME), 1, 4);
    }
    captions.drain();
    assert_eq!(recorder.visible(2999).len(), 1);
    assert!(recorder.visible(3000).is_empty());
    assert_eq!(recorder.visible(4500), ["{\\an1\\pos(192,972)}BYE"]);
    assert!(recorder.visible(5000).is_empty());
    // A flush (seek) clears the track.
    assert_eq!(captions.flush(), S_OK);
    assert_eq!(recorder.events(), 0);
    assert_eq!(
        recorder.0.lock().unwrap().log.last(),
        Some(&LiveCommand::Clear)
    );
    assert!(captions.cues().is_empty());
    assert_eq!(captions.stats().flushes, 1);
    // Detaching drops the overlay's track.
    captions.detach();
    assert_eq!(recorder.0.lock().unwrap().generation, 0);
}

#[test]
fn attaching_replays_what_was_kept_and_a_gone_overlay_detaches() {
    let slots = hello_stream();
    let captions = Captions::new();
    for frame in 0..110 {
        let data = packet(slots.get(&frame).copied(), 0, 0);
        captions.push(&data, Some(frame as i64 * FRAME), 1, 0);
    }
    // HELLO has ended; BYE is being loaded but not shown yet.
    assert_eq!(
        summary(&captions.cues()),
        [(1000, Some(3000), "HELLO".to_owned())]
    );
    let recorder = Recorder::new(1);
    captions.attach(recorder.sink(), 1, ConvertOptions::default(), 0);
    assert_eq!(recorder.visible(1500).len(), 1);
    // A newer attach elsewhere: this overlay is told nothing more, the new one gets everything.
    let second = Recorder::new(2);
    captions.attach(second.sink(), 2, ConvertOptions::default(), ass::FLAG_PLAIN);
    for frame in 110..180 {
        let data = packet(slots.get(&frame).copied(), 0, 0);
        captions.push(&data, Some(frame as i64 * FRAME), 1, 0);
    }
    captions.drain();
    assert!(recorder.visible(4500).is_empty());
    assert_eq!(second.visible(1500), ["{\\an1\\pos(192,972)}HELLO"]);
    assert_eq!(second.visible(4500), ["{\\an1\\pos(192,972)}BYE"]);
    // The overlay moved on (another track loaded): the next post finds out and detaches.
    second.0.lock().unwrap().gone = true;
    captions.flush();
    assert!(captions.lock().live.is_none());
    assert_eq!(
        captions.attach(second.sink(), 2, ConvertOptions::default(), 2),
        E_INVALIDARG
    );
}

#[test]
fn roll_up_text_shows_as_it_arrives() {
    let slots = schedule(&[
        (0, [code(RU2), code(CR), code(ROW15), text("ABCD")].concat()),
        (30, [code(CR), code(ROW15), text("EF")].concat()),
    ]);
    let captions = Captions::new();
    run(&captions, &slots, &(0..60).collect::<Vec<_>>(), 4, 0);
    let cues = captions.cues();
    let texts: Vec<String> = cues.iter().map(Cue::text).collect();
    // The carriage return at 1 s moves the line up a row before the next one starts.
    assert_eq!(texts, ["AB", "ABCD", "ABCD", "ABCD\nEF"]);
    // "AB" arrives in frame 6 (after RU2, CR and the PAC, each twice), "CD" in frame 7.
    assert_eq!((cues[0].start_ms, cues[1].start_ms), (200, 233));
    assert_eq!(cues[1].end_ms, Some(1000));
    assert_eq!((cues[1].rows[0].row, cues[2].rows[0].row), (15, 14));
    assert_eq!(cues[3].start_ms, to_ms(34 * FRAME));
    assert!(cues.iter().all(|cue| cue.kind == CueKind::RollUp));
}

#[test]
fn a_paint_on_caption_without_data_for_16_seconds_is_erased() {
    let slots = schedule(&[(0, [code(RDC), code(ROW15), text("STUCK")].concat())]);
    let captions = Captions::new();
    // Twenty seconds of frames, the last caption data in frame 6.
    run(&captions, &slots, &(0..600).collect::<Vec<_>>(), 4, 0);
    let cues = captions.cues();
    assert_eq!(cues.last().map(Cue::text).as_deref(), Some("STUCK"));
    let last_data = to_ms(6 * FRAME);
    assert_eq!(last_data, 200);
    assert_eq!(
        cues.last().and_then(|cue| cue.end_ms),
        Some(last_data + 16_000)
    );
    // A pop-on caption has no such timeout.
    let slots = schedule(&[(0, [code(ROW15), text("POP"), code(EOC)].concat())]);
    let captions = Captions::new();
    run(&captions, &slots, &(0..600).collect::<Vec<_>>(), 4, 0);
    assert_eq!(captions.cues().last().and_then(|cue| cue.end_ms), None);
}

#[test]
fn a_long_caption_is_extended_while_it_stays() {
    let slots = schedule(&[(0, [code(ROW15), text("LONG"), code(EOC)].concat())]);
    let captions = Captions::new();
    let recorder = Recorder::new(3);
    captions.attach(recorder.sink(), 3, ConvertOptions::default(), 0);
    // 50 seconds at 30 fps.
    run(&captions, &slots, &(0..1500).collect::<Vec<_>>(), 4, 0);
    assert_eq!(recorder.visible(49_000).len(), 1);
    let extensions = recorder
        .0
        .lock()
        .unwrap()
        .log
        .iter()
        .filter(|command| matches!(command, LiveCommand::SetEnd { .. }))
        .count();
    assert!(extensions >= 2, "{extensions}");
    // Still bounded: well before 50 s + 30 s it is gone.
    assert!(recorder.visible(90_000).is_empty());
}

#[test]
fn channels_are_selected_and_seen() {
    // CC3 is field 2 (its misc codes may use 15 for the first byte).
    let slots = schedule(&[(
        0,
        [
            code([0x15, 0x20]),
            code(ROW15),
            text("THREE"),
            code([0x15, 0x2F]),
        ]
        .concat(),
    )]);
    let captions = Captions::new();
    run(&captions, &slots, &(0..30).collect::<Vec<_>>(), 4, 1);
    assert!(captions.cues().is_empty(), "CC1 shows nothing");
    assert_eq!(captions.seen(), SEEN_CC3 | SEEN_CC_DATA);
    let captions = Captions::new();
    assert_eq!(captions.select(3, 0), S_OK);
    run(&captions, &slots, &(0..30).collect::<Vec<_>>(), 4, 1);
    assert_eq!(
        captions.cues().last().map(Cue::text).as_deref(),
        Some("THREE")
    );
    // Selecting another channel starts over and clears an attached overlay.
    let recorder = Recorder::new(9);
    captions.attach(recorder.sink(), 9, ConvertOptions::default(), 0);
    assert_eq!(recorder.events(), 1);
    assert_eq!(captions.select(1, 0), S_OK);
    assert_eq!(recorder.events(), 0);
    assert!(captions.cues().is_empty());
    assert_eq!(captions.select(0, 0), E_INVALIDARG);
    assert_eq!(captions.select(5, 0), E_INVALIDARG);
    assert_eq!(captions.select(1, 1), E_NOTIMPL);
    assert_eq!(captions.select(1, 64), E_INVALIDARG);
    // DTVCC data is seen, not decoded.
    let captions = Captions::new();
    let cc = [0x41, 0xFF, 0xFE, 0x01, 0x02, 0xFF];
    let sei = h264_sei(&[sei_message(&ga94_payload(&cc))]);
    captions.push(&annex_b(&[&sei]), Some(0), 1, 0);
    assert_eq!(captions.seen(), SEEN_DTVCC | SEEN_CC_DATA);
}

#[test]
fn tokens_route_pushes_and_flushes() {
    let first = Captions::new();
    let second = Captions::new();
    let token = 0xCC00_0001;
    let data = packet(Some([0x41, 0x42]), 0, 0);
    assert_eq!(push(token, &data, 0, 1, 0), E_NOT_FOUND);
    assert_eq!(flush(token), E_NOT_FOUND);
    assert_eq!(first.register(token), S_OK);
    assert_eq!(push(token, &data, 0, 1, 0), S_OK);
    assert_eq!(push(token, &data, i64::MIN, 1, 0), S_OK);
    assert_eq!(first.stats().packets, 2);
    assert_eq!(flush(token), S_OK);
    assert_eq!(first.stats().flushes, 1);
    // The token moves to the decoder that registers it last.
    assert_eq!(second.register(token), S_OK);
    assert_eq!(push(token, &data, 0, 1, 0), S_OK);
    assert_eq!(second.stats().packets, 1);
    assert_eq!(first.stats().packets, 2);
    // Registering again starts a new stream: counts and seen bits go.
    assert_eq!(second.register(token + 1), S_OK);
    assert_eq!(
        second.stats(),
        BaleCcStats {
            depth: reorder::DEFAULT_DEPTH as u32,
            ..BaleCcStats::default()
        }
    );
    assert_eq!(push(token, &data, 0, 1, 0), E_NOT_FOUND);
    // A destroyed decoder is gone from the registry.
    drop(second);
    assert_eq!(push(token + 1, &data, 0, 1, 0), E_NOT_FOUND);
    assert_eq!(first.register(0), S_OK);
    assert_eq!(push(0, &data, 0, 1, 0), E_NOT_FOUND);
}

#[test]
fn bad_codecs_and_sizes_are_refused() {
    let captions = Captions::new();
    for (codec, size) in [(0, 0), (4, 0), (1, 5), (2, 8), (3, 4)] {
        assert_eq!(
            captions.push(&[0, 0, 1], Some(0), codec, size),
            E_INVALIDARG
        );
    }
    for (codec, size) in [(1, 0), (1, 3), (2, 4), (3, 0)] {
        assert_eq!(captions.push(&[0, 0, 1], Some(0), codec, size), S_OK);
    }
    // Packets without captions cost nothing and move nothing.
    assert_eq!(captions.stats().caption_packets, 0);
    assert!(captions.cues().is_empty());
}
