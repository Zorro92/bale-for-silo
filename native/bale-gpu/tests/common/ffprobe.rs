//! A clip's first video stream as the FFmpegInteropX caption tap hands it to `bale_cc_push`, read
//! with the local `ffprobe` (no FFmpeg library is linked): every packet's bytes and PTS in 100 ns
//! with the container's start time removed (the `MediaStreamSample` timeline), the codec number
//! and the NAL length size from avcC/hvcC (0 for Annex B and MPEG-2).

use std::path::Path;
use std::process::Command;

pub struct Packet {
    pub data: Vec<u8>,
    pub pts: Option<i64>,
}

pub struct Stream {
    /// `bale_cc_push` codec: 1 H.264, 2 HEVC, 3 MPEG-2.
    pub codec: u32,
    pub nal_length_size: u32,
    pub packets: Vec<Packet>,
}

/// Whether an `ffprobe` runs here.
pub fn available() -> bool {
    Command::new("ffprobe")
        .arg("-version")
        .output()
        .is_ok_and(|output| output.status.success())
}

pub fn read(path: &Path) -> Result<Stream, String> {
    let output = Command::new("ffprobe")
        .args([
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=codec_name,time_base,extradata:format=start_time:packet=pts,data",
            "-show_data",
            "-of",
            "default",
        ])
        .arg(path)
        .output()
        .map_err(|error| format!("ffprobe did not run: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "ffprobe failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    parse(&String::from_utf8_lossy(&output.stdout))
}

#[derive(Default)]
struct Section {
    fields: Vec<(String, String)>,
    /// The bytes of the field whose hexdump follows (`data` or `extradata`).
    bytes: Vec<u8>,
}

impl Section {
    fn get(&self, key: &str) -> Option<&str> {
        self.fields
            .iter()
            .find(|(name, _)| name == key)
            .map(|(_, value)| value.as_str())
    }
}

/// `00000010: 03c0 113f 2e02 ...  ...?.. .`: the bytes of one hexdump line.
fn hexdump_line(line: &str, out: &mut Vec<u8>) -> bool {
    let bytes = line.as_bytes();
    if bytes.len() < 10 || bytes[8] != b':' || !bytes[..8].iter().all(u8::is_ascii_hexdigit) {
        return false;
    }
    let hex = line.get(10..line.len().min(49)).unwrap_or("");
    for group in hex.split_whitespace() {
        for pair in group.as_bytes().chunks(2) {
            let Ok(text) = std::str::from_utf8(pair) else {
                return false;
            };
            match u8::from_str_radix(text, 16) {
                Ok(byte) => out.push(byte),
                Err(_) => return false,
            }
        }
    }
    true
}

fn parse(text: &str) -> Result<Stream, String> {
    let mut packets = Vec::new();
    let mut stream = None;
    let mut format = None;
    // Sections nest ([SIDE_DATA] inside a packet or stream, a [PROGRAM]'s streams); the innermost
    // takes the fields that follow.
    let mut open: Vec<(String, Section)> = Vec::new();
    for line in text.lines() {
        if let Some((_, section)) = open.last_mut()
            && hexdump_line(line, &mut section.bytes)
        {
            continue;
        }
        if let Some(name) = line
            .strip_prefix("[/")
            .and_then(|rest| rest.strip_suffix(']'))
        {
            let Some((kind, section)) = open.pop() else {
                continue;
            };
            // A program's own streams repeat the top-level ones.
            let nested = !open.is_empty();
            match (kind.as_str(), nested) {
                _ if kind != name => {}
                ("PACKET", false) => packets.push(section),
                ("STREAM", false) => stream = Some(section),
                ("FORMAT", false) => format = Some(section),
                _ => {}
            }
        } else if let Some(name) = line
            .strip_prefix('[')
            .and_then(|rest| rest.strip_suffix(']'))
        {
            open.push((name.to_owned(), Section::default()));
        } else if let Some((_, section)) = open.last_mut()
            && let Some((key, value)) = line.split_once('=')
        {
            section.fields.push((key.to_owned(), value.to_owned()));
        }
    }
    let stream = stream.ok_or("no video stream")?;
    let codec = match stream.get("codec_name") {
        Some("h264") => 1,
        Some("hevc") => 2,
        Some("mpeg2video") => 3,
        other => return Err(format!("not a tapped codec: {other:?}")),
    };
    let extradata = &stream.bytes;
    let nal_length_size = match (codec, extradata.first()) {
        // avcC / hvcC (configurationVersion 1); Annex B parameter sets start with a start code.
        (1, Some(1)) if extradata.len() > 4 => u32::from(extradata[4] & 3) + 1,
        (2, Some(1)) if extradata.len() > 21 => u32::from(extradata[21] & 3) + 1,
        _ => 0,
    };
    let (num, den) = stream
        .get("time_base")
        .and_then(|base| base.split_once('/'))
        .and_then(|(num, den)| Some((num.parse::<i128>().ok()?, den.parse::<i128>().ok()?)))
        .filter(|&(num, den)| num > 0 && den > 0)
        .ok_or("no time base")?;
    let start = format
        .as_ref()
        .and_then(|format| format.get("start_time"))
        .and_then(|start| start.parse::<f64>().ok())
        .map_or(0, |seconds| (seconds * 1e7).round() as i64);
    let packets = packets
        .into_iter()
        .map(|section| Packet {
            pts: section
                .get("pts")
                .and_then(|pts| pts.parse::<i128>().ok())
                .map(|pts| (pts * 10_000_000 * num / den) as i64 - start),
            data: section.bytes,
        })
        .collect();
    Ok(Stream {
        codec,
        nal_length_size,
        packets,
    })
}
