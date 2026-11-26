//! Prints the closed captions of a local clip as Bale decodes them: the first video stream as the
//! FFmpegInteropX tap hands it to `bale_cc_push` (read with the local `ffprobe`), through
//! `bale_gpu::subs::cc`. A Linux debug path for caption streams that do not look right.
//!
//! cargo run --release --example cc_dump -- <clip> [cc1|cc2|cc3|cc4] [--ass|--box]
//!
//! `--ass` prints the ASS script the overlay would draw (the converters' default style) instead of
//! the cues; `--box` prints it in Bale's default subtitle appearance (Large, white on a 75% black
//! box: Bale.Core `SubtitleTextStyle.For(SubtitleAppearance.Default)`), whose backgrounds are box
//! events. Render either over a picture with `ffmpeg -vf ass=<script>` to look at it.

#[path = "../tests/common/ffprobe.rs"]
mod ffprobe;

use std::path::Path;
use std::process::ExitCode;

use bale_gpu::subs::cc::cue::{Cue, CueRow};
use bale_gpu::subs::cc::{Captions, ass};
use bale_gpu::subs::convert::{ConvertOptions, TextStyle};

/// Bale.Core `SubtitleTextStyle.For(SubtitleAppearance.Default)`: Large (60), white, a black box at
/// 75% padded by an eighth of the size, no outline.
const DEFAULT_APPEARANCE: TextStyle = TextStyle {
    font_size: 60,
    primary_colour: 0x00FF_FFFF,
    outline_colour: 0,
    back_colour: 0x4000_0000,
    border_style: 4,
    outline: 0.0,
    shadow: 7.5,
    alignment: 2,
    margin_v: 54,
};

fn usage() -> ExitCode {
    eprintln!("usage: cc_dump <clip> [cc1|cc2|cc3|cc4] [--ass|--box]");
    ExitCode::from(2)
}

fn time(ms: i64) -> String {
    format!("{}:{:02}.{:03}", ms / 60_000, ms / 1000 % 60, ms % 1000)
}

fn describe(row: &CueRow) -> String {
    row.segments
        .iter()
        .map(|segment| {
            let style = segment.style;
            let mut tags = Vec::new();
            if style.foreground != Default::default() {
                tags.push(format!("{:?}", style.foreground));
            }
            if style.italic {
                tags.push("italic".to_owned());
            }
            if style.underline {
                tags.push("underline".to_owned());
            }
            if style.flash {
                tags.push("flash".to_owned());
            }
            if style.background != Default::default() {
                tags.push(format!(
                    "on {:?} {:?}",
                    style.background.color, style.background.opacity
                ));
            }
            if tags.is_empty() {
                segment.text.clone()
            } else {
                format!("[{}]{}", tags.join(" "), segment.text)
            }
        })
        .collect()
}

fn main() -> ExitCode {
    let mut args = std::env::args().skip(1);
    let Some(path) = args.next() else {
        return usage();
    };
    let (mut channel, mut script) = (1, None);
    for arg in args {
        match arg.as_str() {
            "--ass" => script = Some(ConvertOptions::default()),
            "--box" => script = Some(ConvertOptions::styled(&DEFAULT_APPEARANCE, None)),
            other => match other.strip_prefix("cc").and_then(|n| n.parse().ok()) {
                Some(n @ 1..=4) => channel = n,
                _ => return usage(),
            },
        }
    }
    if !ffprobe::available() {
        eprintln!("cc_dump reads the clip with ffprobe, which is not installed");
        return ExitCode::FAILURE;
    }
    let stream = match ffprobe::read(Path::new(&path)) {
        Ok(stream) => stream,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::FAILURE;
        }
    };
    let captions = Captions::new();
    captions.select(channel, 0);
    // The decoder keeps half a minute of ended cues: collect them as they come.
    let mut cues: Vec<Cue> = Vec::new();
    let mut collect = |captions: &Captions| {
        for cue in captions.cues() {
            if cue.end_ms.is_some() && !cues.contains(&cue) {
                cues.push(cue);
            }
        }
    };
    for (index, packet) in stream.packets.iter().enumerate() {
        captions.push(
            &packet.data,
            packet.pts,
            stream.codec,
            stream.nal_length_size,
        );
        if index % 256 == 255 {
            collect(&captions);
        }
    }
    captions.drain();
    collect(&captions);
    cues.extend(
        captions
            .cues()
            .into_iter()
            .filter(|cue| cue.end_ms.is_none()),
    );
    let stats = captions.stats();
    eprintln!(
        "{} packets ({} with cc_data), codec {} NAL length {}, seen 0x{:03x}, reorder depth {}, late {}, {} cues",
        stats.packets,
        stats.caption_packets,
        stream.codec,
        stream.nal_length_size,
        stats.seen,
        stats.depth,
        stats.late,
        cues.len()
    );
    if let Some(options) = script {
        print!("{}", ass::script(&cues, &options, 0, 5000));
        return ExitCode::SUCCESS;
    }
    for cue in &cues {
        let end = cue.end_ms.map_or_else(|| "open".to_owned(), time);
        println!("{} - {} {:?}", time(cue.start_ms), end, cue.kind);
        for row in &cue.rows {
            println!(
                "    row {:2} col {:2}  {}",
                row.row,
                row.first,
                describe(row)
            );
        }
    }
    ExitCode::SUCCESS
}
