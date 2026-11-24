//! Dolby TrueHD and MLP in IEC 61937-9 MAT frames.
//!
//! A MAT frame is one burst of 61440 bytes (3840 8-channel frames at 192 kHz, 1/50 s): the preamble, 61424
//! bytes of MAT data and 8 bytes of stuffing. The data opens with a start code, has a middle code at byte
//! 30708 (30716 in the burst, 4 bytes before its middle) and closes with an end code in its last 16 bytes;
//! access units fill the rest, split across the codes and across frames where they fall.
//!
//! Timing is kept by the bytes themselves: every 1/1200 s of the access units' `input_timing` owns 2560
//! bytes of the stream (61440 per 1/50 s, so 24 units of 40 samples at 48 kHz). Before each unit go zero
//! bytes for the time its predecessor left unused, so a variable-bitrate stream still lands every unit at
//! its time. Codes and the 16 bytes between two frames' data (stuffing, then the next preamble) take time
//! too: they count as padding while padding is due, and as part of the unit otherwise. A gap that is
//! negative or longer than half a frame is a discontinuity and gets no padding.
//!
//! Code values, code positions, the padding rule and its limit follow FFmpeg's `spdifenc.c`, so the
//! bytes match its muxer; VLC's `tospdif.c` uses the same codes with fixed 2560-byte slots instead.

use super::{Burst, Carrier, Error, Packer, Preamble, Subtype, Words, write_burst};

const DATA_TYPE_MAT: u16 = 22;
const BURST_BYTES: usize = 61440;
const DATA_BYTES: usize = 61424;
/// Timeline bytes between one frame's data and the next's: this frame's stuffing and the next preamble.
const FRAME_GAP: usize = BURST_BYTES - DATA_BYTES;
/// Stream bytes per 1/1200 s of input timing (1/1102.5 s for the 44.1 kHz family).
const BYTES_PER_UNIT_TIME: usize = 2560;
/// Padding at or above this is a timing discontinuity, not a gap to fill.
const MAX_PADDING: usize = DATA_BYTES / 2;

pub(super) const START_CODE: [u8; 20] = [
    0x07, 0x9E, 0x00, 0x03, 0x84, 0x01, 0x01, 0x01, 0x80, 0x00, 0x56, 0xA5, 0x3B, 0xF4, 0x81, 0x83,
    0x49, 0x80, 0x77, 0xE0,
];
pub(super) const MIDDLE_CODE: [u8; 12] = [
    0xC3, 0xC1, 0x42, 0x49, 0x3B, 0xFA, 0x82, 0x83, 0x49, 0x80, 0x77, 0xE0,
];
pub(super) const END_CODE: [u8; 16] = [
    0xC3, 0xC2, 0xC0, 0xC4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x97, 0x11,
];

struct Code {
    /// Offset in the MAT data.
    at: usize,
    bytes: &'static [u8],
}

const CODES: [Code; 3] = [
    Code {
        at: 0,
        bytes: &START_CODE,
    },
    Code {
        at: 30708,
        bytes: &MIDDLE_CODE,
    },
    Code {
        at: DATA_BYTES - END_CODE.len(),
        bytes: &END_CODE,
    },
];

/// The major sync signature at byte 4 of an access unit, followed by 0xBA (TrueHD) or 0xBB (MLP).
const MAJOR_SYNC: [u8; 3] = [0xF8, 0x72, 0x6F];

pub(super) fn carrier(sample_rate: u32) -> Result<Carrier, Error> {
    match sample_rate {
        48_000 | 96_000 | 192_000 => Ok(Carrier::new(Subtype::DolbyMlp, 8, 192_000)),
        44_100 | 88_200 | 176_400 => Ok(Carrier::new(Subtype::DolbyMlp, 8, 176_400)),
        _ => Err(Error::Unsupported),
    }
}

/// What a major sync says: samples per access unit (1/1200 s: 40, 80 or 160) and the carrier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Rate {
    unit_samples: usize,
    carrier: Carrier,
}

/// The rate from the major sync at the front of `unit`, if it has one. The sampling-frequency code is the
/// high nibble of byte 8 (TrueHD) or 9 (MLP): 0..2 for 48, 96 and 192 kHz, 8..10 for the 44.1 kHz family.
fn major_sync(unit: &[u8]) -> Result<Option<Rate>, Error> {
    if unit.get(4..7) != Some(&MAJOR_SYNC[..]) {
        return Ok(None);
    }
    let code = match unit.get(7) {
        Some(0xBA) => unit.get(8),
        Some(0xBB) => unit.get(9),
        _ => return Err(Error::Header),
    }
    .ok_or(Error::Truncated)?
        >> 4;
    let sample_rate = match code {
        0 => 48_000,
        1 => 96_000,
        2 => 192_000,
        8 => 44_100,
        9 => 88_200,
        10 => 176_400,
        _ => return Err(Error::Header),
    };
    Ok(Some(Rate {
        unit_samples: 40 << (code & 0x03),
        carrier: carrier(sample_rate)?,
    }))
}

/// Splits the access unit at the front of `bytes` by its length field (the low 12 bits of the first word,
/// in 16-bit words).
fn access_unit(bytes: &[u8]) -> Result<(&[u8], &[u8]), Error> {
    let [high, low, ..] = *bytes else {
        return Err(Error::Truncated);
    };
    let length = (usize::from(high & 0x0F) << 8 | usize::from(low)) * 2;
    if length < 4 {
        return Err(Error::Header);
    }
    if length > bytes.len() {
        return Err(Error::Truncated);
    }
    Ok(bytes.split_at(length))
}

/// The MAT frame being filled.
#[derive(Debug)]
struct MatFrame {
    data: Box<[u8]>,
    len: usize,
    /// Index into [`CODES`] of the next code to write.
    next_code: usize,
    /// Whether access-unit bytes are in it, not just codes and padding.
    has_audio: bool,
}

impl MatFrame {
    fn new() -> Self {
        Self {
            data: vec![0; DATA_BYTES].into_boxed_slice(),
            len: 0,
            next_code: 0,
            has_audio: false,
        }
    }

    fn clear(&mut self) {
        self.len = 0;
        self.next_code = 0;
        self.has_audio = false;
    }

    /// Writes `count` bytes (zeros without `from`), never past the next code.
    fn write(&mut self, from: Option<&[u8]>, count: usize) {
        let to = &mut self.data[self.len..self.len + count];
        match from {
            Some(bytes) => to.copy_from_slice(&bytes[..count]),
            None => to.fill(0),
        }
        self.len += count;
    }

    /// Writes the next code; a frame completed by its end code is handed out and the next one begun.
    /// Returns the timeline bytes the code took, the gap to the next frame included.
    fn write_code(&mut self, carrier: Carrier, bursts: &mut Vec<Burst>) -> usize {
        let code = &CODES[self.next_code];
        self.write(Some(code.bytes), code.bytes.len());
        self.next_code += 1;
        if self.next_code < CODES.len() {
            return code.bytes.len();
        }
        bursts.push(self.burst(carrier));
        self.clear();
        code.bytes.len() + FRAME_GAP
    }

    fn burst(&self, carrier: Carrier) -> Burst {
        // DATA_BYTES fits a 16-bit Pd, and the payload fits the burst by construction.
        let preamble = Preamble {
            pc: DATA_TYPE_MAT,
            pd: DATA_BYTES as u16,
        };
        write_burst(
            carrier,
            Some(preamble),
            &self.data,
            Words::BigEndian,
            BURST_BYTES,
        )
    }

    /// Lays `padding` timeline bytes and then `unit` into the frames, writing each code where it falls
    /// and handing out every frame that completes. Returns the unit's footprint: its own bytes plus the
    /// code and gap bytes charged to it rather than to the padding.
    fn place(
        &mut self,
        mut padding: usize,
        mut unit: &[u8],
        carrier: Carrier,
        bursts: &mut Vec<Burst>,
    ) -> usize {
        let mut footprint = unit.len();
        loop {
            let next_code_at = CODES[self.next_code].at;
            if self.len == next_code_at {
                let span = self.write_code(carrier, bursts);
                let absorbed = span.min(padding);
                padding -= absorbed;
                footprint += span - absorbed;
            } else if padding > 0 {
                let count = padding.min(next_code_at - self.len);
                self.write(None, count);
                padding -= count;
            } else if !unit.is_empty() {
                let count = unit.len().min(next_code_at - self.len);
                self.write(Some(unit), count);
                unit = &unit[count..];
                self.has_audio = true;
            } else {
                return footprint;
            }
        }
    }

    /// Zero-fills the rest of the frame around its remaining codes and hands it out.
    fn complete(&mut self, carrier: Carrier) -> Burst {
        while self.next_code < CODES.len() {
            let gap = CODES[self.next_code].at - self.len;
            self.write(None, gap);
            let code = &CODES[self.next_code];
            self.write(Some(code.bytes), code.bytes.len());
            self.next_code += 1;
        }
        let burst = self.burst(carrier);
        self.clear();
        burst
    }
}

/// Dolby TrueHD (Atmos included: its objects ride inside the access units) and MLP: 24 access units of
/// 1/1200 s per MAT frame, one IEC 61937-9 burst (data type 22, Pd = 61424 bytes) every 1/50 s, over an
/// 8-channel 192 kHz carrier (176.4 kHz for 44.1 kHz-family streams).
#[derive(Debug)]
pub struct TrueHdPacker {
    rate: Option<Rate>,
    /// The previous unit's `input_timing` and footprint.
    last: Option<(u16, usize)>,
    frame: MatFrame,
}

impl TrueHdPacker {
    pub fn new() -> Self {
        Self {
            rate: None,
            last: None,
            frame: MatFrame::new(),
        }
    }
}

impl Default for TrueHdPacker {
    fn default() -> Self {
        Self::new()
    }
}

impl Packer for TrueHdPacker {
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error> {
        // Check every access unit first, so a bad one leaves the packer as it was. Bytes after the last
        // whole unit are dropped.
        let mut units = Vec::with_capacity(1);
        let mut rate = self.rate;
        let mut rest = unit;
        while !rest.is_empty() {
            let (bytes, tail) = match access_unit(rest) {
                Ok(split) => split,
                Err(error) if units.is_empty() => return Err(error),
                Err(_) => break,
            };
            if let Some(found) = major_sync(bytes)? {
                rate = Some(found);
            }
            units.push((bytes, rate.ok_or(Error::AwaitingMajorSync)?));
            rest = tail;
        }
        if units.is_empty() {
            return Err(Error::Truncated);
        }
        self.rate = rate;
        let mut bursts = Vec::new();
        for (bytes, rate) in units {
            let timing = u16::from_be_bytes([bytes[2], bytes[3]]);
            let padding = self.last.map_or(0, |(last_timing, last_footprint)| {
                let due = usize::from(timing.wrapping_sub(last_timing)) * BYTES_PER_UNIT_TIME
                    / rate.unit_samples;
                due.checked_sub(last_footprint)
                    .filter(|&padding| padding < MAX_PADDING)
                    .unwrap_or(0)
            });
            let footprint = self.frame.place(padding, bytes, rate.carrier, &mut bursts);
            self.last = Some((timing, footprint));
        }
        Ok(bursts)
    }

    fn flush(&mut self) -> Vec<Burst> {
        let bursts = match self.rate {
            Some(rate) if self.frame.has_audio => vec![self.frame.complete(rate.carrier)],
            _ => Vec::new(),
        };
        self.reset();
        bursts
    }

    fn reset(&mut self) {
        self.frame.clear();
        self.last = None;
    }

    fn carrier(&self) -> Option<Carrier> {
        self.rate.map(|rate| rate.carrier)
    }
}
