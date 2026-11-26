//! The CEA-608 test clips (bale-subtitle-clips 10-15: H.264 in MP4, MPEG-TS and MKV) through the
//! caption decoder as the fork's tap feeds it, demuxed by the local `ffprobe`, against the captions
//! their generator wrote (`generator/cea608.py`; the rich and split clips by their caption
//! commands). With `ffmpeg`, the rich clip is also re-encoded with B-frames as H.264 (MP4, TS),
//! HEVC (MKV, TS) and MPEG-2 (TS), whose packets arrive out of presentation order.
//!
//! Skipped when the clips (`BALE_SUBTITLE_CLIPS`, default `$HOME/Videos/bale-subtitle-clips`) or
//! `ffprobe` are missing, as on CI. The clips are never copied into the repository.

#[path = "common/ffprobe.rs"]
mod ffprobe;

use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, Mutex, Weak};
use std::time::Instant;

use bale_gpu::subs::S_OK;
use bale_gpu::subs::cc::cea608::{Color, Opacity};
use bale_gpu::subs::cc::cue::{Cue, CueRow};
use bale_gpu::subs::cc::{BaleCcStats, Captions, LiveCommand, LiveSink, SEEN_CC_DATA, SEEN_CC1};
use bale_gpu::subs::convert::ConvertOptions;

/// One frame either side: the MKV clips start their video 23 ms in.
const TOLERANCE_MS: i64 = 40;

fn clips() -> Option<PathBuf> {
    let dir = std::env::var_os("BALE_SUBTITLE_CLIPS")
        .map(PathBuf::from)
        .or_else(|| {
            std::env::var_os("HOME").map(|home| Path::new(&home).join("Videos/bale-subtitle-clips"))
        })?;
    dir.is_dir().then_some(dir)
}

/// Decodes `path` on CC1 and returns its cues, the live events an overlay would have shown and
/// the decoder's counters.
fn decode(path: &Path) -> (Vec<Cue>, Arc<Recorder>, BaleCcStats) {
    let stream = ffprobe::read(path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
    let captions = Captions::new();
    let recorder = Arc::new(Recorder::default());
    let sink: Arc<dyn LiveSink> = recorder.clone();
    let sink: Weak<dyn LiveSink> = Arc::downgrade(&sink);
    assert_eq!(captions.attach(sink, 1, ConvertOptions::default(), 0), S_OK);
    let started = Instant::now();
    for packet in &stream.packets {
        let hr = captions.push(
            &packet.data,
            packet.pts,
            stream.codec,
            stream.nal_length_size,
        );
        assert_eq!(hr, S_OK);
    }
    captions.drain();
    // What the tap's thread spends per packet (this host; the console's cores are slower).
    let bytes: usize = stream.packets.iter().map(|packet| packet.data.len()).sum();
    println!(
        "  {}: {:.1} µs per packet, {:.0} KiB per packet",
        path.file_name().unwrap_or_default().to_string_lossy(),
        started.elapsed().as_secs_f64() * 1e6 / stream.packets.len().max(1) as f64,
        bytes as f64 / 1024.0 / stream.packets.len().max(1) as f64
    );
    let stats = captions.stats();
    assert_eq!(stats.packets, stream.packets.len() as u64);
    (captions.cues(), recorder, stats)
}

/// The events an attached overlay was given.
#[derive(Default)]
struct Recorder(Mutex<Vec<(i32, i64, i64, String)>>);

impl LiveSink for Recorder {
    fn live(&self, _generation: u64, commands: &mut Vec<LiveCommand>) -> i32 {
        let mut events = self.0.lock().unwrap();
        for command in commands.drain(..) {
            match command {
                LiveCommand::Add {
                    id,
                    start_ms,
                    end_ms,
                    text,
                } => events.push((id, start_ms, end_ms, text)),
                LiveCommand::SetEnd { id, end_ms } => {
                    if let Some(event) = events.iter_mut().find(|event| event.0 == id) {
                        event.2 = end_ms;
                    }
                }
                LiveCommand::Clear => events.clear(),
            }
        }
        S_OK
    }

    fn end_live(&self, _generation: u64) {}
}

impl Recorder {
    fn showing(&self, t_ms: i64) -> usize {
        self.0
            .lock()
            .unwrap()
            .iter()
            .filter(|(_, start, end, _)| *start <= t_ms && t_ms < *end)
            .count()
    }
}

/// A caption the clip shows from `start` to `end` (ms) with these rows.
struct Shown {
    start: i64,
    end: i64,
    rows: &'static [(u8, &'static str)],
}

/// What is displayed at `at` (ms), for the roll-up and paint-on parts, which change per frame.
struct At {
    at: i64,
    rows: &'static [(u8, &'static str)],
}

fn rows_of(cue: &Cue) -> Vec<(u8, String)> {
    cue.rows.iter().map(|row| (row.row, row.text())).collect()
}

fn expected(rows: &[(u8, &str)]) -> Vec<(u8, String)> {
    rows.iter()
        .map(|&(row, text)| (row, text.to_owned()))
        .collect()
}

fn shown_at(cues: &[Cue], t: i64) -> Option<&Cue> {
    cues.iter()
        .find(|cue| cue.start_ms <= t && cue.end_ms.is_none_or(|end| t < end))
}

/// Checks `cues` against the pop-on captions and the samples; a description of every mismatch.
fn compare(cues: &[Cue], shown: &[Shown], samples: &[At]) -> Vec<String> {
    let mut problems = Vec::new();
    for caption in shown {
        let want = expected(caption.rows);
        let found = cues.iter().find(|cue| {
            (cue.start_ms - caption.start).abs() <= TOLERANCE_MS
                && cue
                    .end_ms
                    .is_some_and(|end| (end - caption.end).abs() <= TOLERANCE_MS)
                && rows_of(cue) == want
        });
        if found.is_none() {
            let near: Vec<String> = cues
                .iter()
                .filter(|cue| (cue.start_ms - caption.start).abs() <= 1000)
                .map(|cue| format!("{:?}-{:?} {:?}", cue.start_ms, cue.end_ms, rows_of(cue)))
                .collect();
            problems.push(format!(
                "{}-{} {want:?} not found; near: {near:?}",
                caption.start, caption.end
            ));
        }
    }
    for sample in samples {
        let want = expected(sample.rows);
        let got = shown_at(cues, sample.at).map(rows_of).unwrap_or_default();
        if got != want {
            problems.push(format!("at {}: {got:?}, expected {want:?}", sample.at));
        }
    }
    problems
}

const PLAIN: &[Shown] = &[
    Shown {
        start: 1000,
        end: 5000,
        rows: &[(15, "608 1/6 - plain text")],
    },
    Shown {
        start: 6000,
        end: 10000,
        rows: &[(14, "608 2/6 - two lines"), (15, "second line here")],
    },
    Shown {
        start: 11000,
        end: 15000,
        rows: &[(15, "608 3/6 - italic words")],
    },
    Shown {
        start: 16000,
        end: 20000,
        rows: &[(15, "608 4/6 - yellow white")],
    },
    Shown {
        start: 21000,
        end: 25000,
        rows: &[(1, "608 5/6 - top of screen")],
    },
    Shown {
        start: 26000,
        end: 29000,
        rows: &[(15, "608 6/6 - last cue")],
    },
];

const RICH: &[Shown] = &[
    Shown {
        start: 4000,
        end: 9000,
        rows: &[
            (11, "PAC green blue cyan"),
            (12, "red yellow magenta white"),
            (13, "underline italic+ul plain"),
            (14, "flashing text"),
            (15, "608 A - colors, ul, flash"),
        ],
    },
    Shown {
        start: 9000,
        end: 14000,
        rows: &[
            (11, "blue box opaque"),
            (12, "red box semi"),
            (13, "transparent, no box"),
            (14, "black on white"),
            (15, "608 B - backgrounds"),
        ],
    },
    Shown {
        start: 14000,
        end: 18000,
        rows: &[
            (12, "indent 8 + tab 2"),
            (13, "sp:♪®°½¿™£ àè"),
            (14, "ext:¡ÉÚßÄö©«» std:ñÑ"),
            (15, "608 C - chars, tab"),
        ],
    },
];

const RICH_LATER: &[At] = &[
    At {
        at: 19_900,
        rows: &[(15, "608 roll-up line 1")],
    },
    At {
        at: 21_500,
        rows: &[(14, "608 roll-up line 1"), (15, "roll-up line 2")],
    },
    At {
        at: 23_000,
        rows: &[
            (13, "608 roll-up line 1"),
            (14, "roll-up line 2"),
            (15, "roll-up line 3"),
        ],
    },
    At {
        at: 24_400,
        rows: &[
            (13, "roll-up line 2"),
            (14, "roll-up line 3"),
            (15, "line 4 - line 1 gone"),
        ],
    },
    At {
        at: 24_800,
        rows: &[],
    },
    At {
        at: 28_500,
        rows: &[(8, "paint-on appears slowly")],
    },
    At {
        at: 29_500,
        rows: &[],
    },
];

const SPLIT: &[Shown] = &[
    Shown {
        start: 2000,
        end: 5000,
        rows: &[(15, "608 throwaway first cue")],
    },
    Shown {
        start: 5000,
        end: 7500,
        rows: &[
            (12, "PAC green blue cyan red"),
            (13, "PAC yellow magenta white"),
            (14, "underline italic+ul plain"),
            (15, "608 A1 - colors, ul"),
        ],
    },
    Shown {
        start: 7500,
        end: 10000,
        rows: &[(14, "flashing text"), (15, "608 A2 - flash")],
    },
    Shown {
        start: 10000,
        end: 12500,
        rows: &[
            (13, "blue box opaque"),
            (14, "red box semi"),
            (15, "608 B1 - bg colors"),
        ],
    },
    Shown {
        start: 12500,
        end: 15000,
        rows: &[(14, "transparent, no box"), (15, "608 B2 - transparent bg")],
    },
    Shown {
        start: 15000,
        end: 17500,
        rows: &[(14, "black on white"), (15, "608 B3 - black text")],
    },
    Shown {
        start: 17500,
        end: 20500,
        rows: &[(15, "608 B4 - plain after bg cues")],
    },
    Shown {
        start: 20500,
        end: 24000,
        rows: &[
            (12, "PAC green blue cyan red"),
            (13, "PAC yellow magenta white"),
            (14, "underline italic+ul plain"),
            (15, "608 A1 - colors, ul"),
        ],
    },
];

fn row(cue: &Cue, number: u8) -> &CueRow {
    cue.rows
        .iter()
        .find(|row| row.row == number)
        .expect("the row")
}

/// The trimmed text of each run of `row` with its colour, italics and underline.
fn runs(row: &CueRow) -> Vec<(String, Color, bool, bool)> {
    row.segments
        .iter()
        .map(|s| {
            (
                s.text.trim().to_owned(),
                s.style.foreground,
                s.style.italic,
                s.style.underline,
            )
        })
        .collect()
}

fn find(cues: &[Cue], start: i64) -> &Cue {
    cues.iter()
        .find(|cue| (cue.start_ms - start).abs() <= TOLERANCE_MS)
        .expect("the caption")
}

/// The styles the plain clips' generator sets: italics in 3/6, yellow in 4/6.
fn check_plain_styles(cues: &[Cue]) {
    let italic = runs(row(find(cues, 11000), 15));
    assert_eq!(
        italic,
        [
            ("608 3/6 -".to_owned(), Color::White, false, false),
            ("italic words".to_owned(), Color::White, true, false)
        ]
    );
    let yellow = runs(row(find(cues, 16000), 15));
    assert_eq!(
        yellow,
        [
            ("608 4/6 -".to_owned(), Color::White, false, false),
            ("yellow".to_owned(), Color::Yellow, false, false),
            ("white".to_owned(), Color::White, false, false)
        ]
    );
}

fn check_rich_styles(cues: &[Cue]) {
    let a = find(cues, 4000);
    let colours: Vec<(String, Color)> = runs(row(a, 11))
        .into_iter()
        .chain(runs(row(a, 12)))
        .map(|(text, colour, _, _)| (text, colour))
        .collect();
    let want: Vec<(String, Color)> = [
        ("PAC green", Color::Green),
        ("blue", Color::Blue),
        ("cyan", Color::Cyan),
        ("red", Color::Red),
        ("yellow", Color::Yellow),
        ("magenta", Color::Magenta),
        ("white", Color::White),
    ]
    .iter()
    .map(|&(text, colour)| (text.to_owned(), colour))
    .collect();
    assert_eq!(colours, want);
    let underline = runs(row(a, 13));
    assert_eq!(
        underline,
        [
            ("underline".to_owned(), Color::White, false, true),
            ("italic+ul".to_owned(), Color::White, true, true),
            ("plain".to_owned(), Color::White, false, false)
        ]
    );
    let b = find(cues, 9000);
    let background = |number| row(b, number).segments[0].style.background;
    assert_eq!(
        (background(11).color, background(11).opacity),
        (Color::Blue, Opacity::Opaque)
    );
    assert_eq!(
        (background(12).color, background(12).opacity),
        (Color::Red, Opacity::SemiTransparent)
    );
    assert_eq!(background(13).opacity, Opacity::Transparent);
    assert_eq!(background(14).color, Color::White);
    assert_eq!(row(b, 14).segments[0].style.foreground, Color::Black);
    let c = find(cues, 14000);
    assert_eq!(row(c, 12).first, 10, "indent 8 + tab 2");
}

/// Re-encodes `source` into `out` with ffmpeg; false when ffmpeg or the encoder is missing.
fn encode(source: &Path, out: &Path, args: &[&str]) -> bool {
    let status = Command::new("ffmpeg")
        .args(["-nostdin", "-v", "error", "-y", "-i"])
        .arg(source)
        .args(["-an", "-vf", "scale=320:180", "-a53cc", "1"])
        .args(args)
        .arg(out)
        .output();
    matches!(status, Ok(output) if output.status.success()) && out.is_file()
}

#[test]
fn the_cea608_clips_decode_to_their_captions() {
    let Some(dir) = clips() else {
        eprintln!("skipped: no bale-subtitle-clips (set BALE_SUBTITLE_CLIPS)");
        return;
    };
    if !ffprobe::available() {
        eprintln!("skipped: no ffprobe");
        return;
    }
    let mut checked = Vec::new();
    let mut failures = Vec::new();
    type Styles = fn(&[Cue]);
    let clips: [(&str, &[Shown], &[At], Styles); 6] = [
        ("10-mp4-cea608.mp4", PLAIN, &[], check_plain_styles),
        ("11-ts-cea608.ts", PLAIN, &[], check_plain_styles),
        ("12-mkv-cea608.mkv", PLAIN, &[], check_plain_styles),
        (
            "13-mp4-cea608-rich.mp4",
            RICH,
            RICH_LATER,
            check_rich_styles,
        ),
        (
            "14-mkv-cea608-rich.mkv",
            RICH,
            RICH_LATER,
            check_rich_styles,
        ),
        ("15-mp4-cea608-split.mp4", SPLIT, &[], |_| {}),
    ];
    let mut rich_source = None;
    for (name, shown, samples, styles) in clips {
        let path = dir.join(name);
        if !path.is_file() {
            eprintln!("{name}: missing, skipped");
            continue;
        }
        let (cues, recorder, stats) = decode(&path);
        let problems = compare(&cues, shown, samples);
        if problems.is_empty() {
            styles(&cues);
            // The overlay was given every caption: something shows mid-caption.
            for caption in shown {
                assert!(
                    recorder.showing((caption.start + caption.end) / 2) > 0,
                    "{name}"
                );
            }
            assert_eq!(stats.late, 0, "{name}");
            println!(
                "{name}: {} captions match ({} cues, {} packets)",
                shown.len() + samples.len(),
                cues.len(),
                stats.packets
            );
            checked.push(name.to_owned());
        } else {
            failures.push(format!("{name}:\n  {}", problems.join("\n  ")));
        }
        if name.starts_with("13-") {
            rich_source = Some(path);
        }
    }
    // The rich clip with B-frames, in every codec and framing the tap hands over.
    if let Some(source) = rich_source {
        let work = Path::new(env!("CARGO_TARGET_TMPDIR")).join("cc-clips");
        let _ = std::fs::create_dir_all(&work);
        let x264 = [
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-bf",
            "3",
            "-x264-params",
            "b-pyramid=normal",
        ];
        let x265 = [
            "-c:v",
            "libx265",
            "-preset",
            "veryfast",
            "-x265-params",
            "bframes=4:b-pyramid=1:log-level=error",
        ];
        let mpeg2 = ["-c:v", "mpeg2video", "-bf", "2", "-g", "15"];
        let variants: [(&str, Vec<&str>); 5] = [
            ("h264-bframes.mp4", x264.to_vec()),
            ("h264-bframes.ts", [&x264[..], &["-f", "mpegts"]].concat()),
            ("hevc-bframes.mkv", x265.to_vec()),
            ("hevc-bframes.ts", [&x265[..], &["-f", "mpegts"]].concat()),
            ("mpeg2-bframes.ts", [&mpeg2[..], &["-f", "mpegts"]].concat()),
        ];
        for (name, args) in variants {
            let out = work.join(name);
            if !encode(&source, &out, &args) {
                eprintln!("{name}: ffmpeg or its encoder is missing, skipped");
                continue;
            }
            let (cues, _, stats) = decode(&out);
            let problems = compare(&cues, RICH, RICH_LATER);
            if problems.is_empty() {
                check_rich_styles(&cues);
                // Reordered without a frame decoded out of turn.
                assert_eq!(stats.late, 0, "{name}");
                println!(
                    "{name}: {} captions match ({} cues, reorder depth {})",
                    RICH.len() + RICH_LATER.len(),
                    cues.len(),
                    stats.depth
                );
                checked.push(name.to_owned());
            } else {
                failures.push(format!("{name}:\n  {}", problems.join("\n  ")));
            }
            let _ = std::fs::remove_file(&out);
        }
        let _ = std::fs::remove_dir(&work);
    }
    println!(
        "{} clips match their captions: {}",
        checked.len(),
        checked.join(", ")
    );
    assert!(failures.is_empty(), "{}", failures.join("\n"));
    // CC1 carried every clip's captions.
    if let Some(path) = ["10-mp4-cea608.mp4", "11-ts-cea608.ts"]
        .iter()
        .map(|name| dir.join(name))
        .find(|path| path.is_file())
    {
        let stream = ffprobe::read(&path).expect("probe");
        let captions = Captions::new();
        for packet in &stream.packets {
            captions.push(
                &packet.data,
                packet.pts,
                stream.codec,
                stream.nal_length_size,
            );
        }
        assert_eq!(captions.seen(), SEEN_CC1 | SEEN_CC_DATA);
    }
}
