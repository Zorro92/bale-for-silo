//! AC-3 and E-AC-3 in IEC 61937-3 bursts. Header fields from ATSC A/52 (5.3 for AC-3, E.1.2 for E-AC-3).

use super::{
    Burst, Carrier, Error, PREAMBLE_BYTES, Packer, Preamble, Subtype, Words, base_rate, burst,
    length_code,
};

const SYNC: [u8; 2] = [0x0B, 0x77];
/// Header bytes read: sync word, then `crc1` (AC-3) or `strmtyp`/`substreamid`/`frmsiz` (E-AC-3), then
/// `fscod` and the frame size or block count, then `bsid` with `bsmod`.
const HEADER_BYTES: usize = 6;

const DATA_TYPE_AC3: u16 = 1;
const DATA_TYPE_EAC3: u16 = 21;

/// Both burst types hold 1536 samples: six audio blocks of 256.
const BLOCKS_PER_BURST: u32 = 6;
/// AC-3 repeats every 1536 IEC 60958 frames of 4 bytes, one per sample.
const AC3_PERIOD_BYTES: usize = 1536 * 4;
/// E-AC-3 runs its carrier at four times the sample rate, so the same 1536 samples take 6144 frames.
const EAC3_PERIOD_BYTES: usize = 6144 * 4;

/// `fscod` 0..2.
const SAMPLE_RATES: [u32; 3] = [48_000, 44_100, 32_000];
/// AC-3 bit rates in kbit/s by `frmsizecod / 2` (A/52 Table 5.18).
const AC3_KBPS: [usize; 19] = [
    32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640,
];
/// E-AC-3 audio blocks per sync frame by `numblkscod`.
const EAC3_BLOCKS: [u32; 4] = [1, 2, 3, 6];

pub(super) fn ac3_carrier(sample_rate: u32) -> Result<Carrier, Error> {
    Ok(Carrier::new(
        Subtype::DolbyDigital,
        2,
        base_rate(sample_rate)?,
    ))
}

pub(super) fn eac3_carrier(sample_rate: u32) -> Result<Carrier, Error> {
    Ok(Carrier::new(
        Subtype::DolbyDigitalPlus,
        2,
        base_rate(sample_rate)? * 4,
    ))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Substream {
    /// An AC-3 frame (`bsid` up to 8): always a program's six blocks.
    Ac3,
    /// E-AC-3 `strmtyp` 0 or 2, with its `substreamid` (the program).
    Independent(u8),
    /// E-AC-3 `strmtyp` 1: more channels for the independent frame in front of it.
    Dependent,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct SyncFrame {
    bytes: usize,
    sample_rate: u32,
    blocks: u32,
    /// AC-3 `bsmod`, which IEC 61937-3 repeats in Pc bits 8..10. E-AC-3 bursts carry none.
    bsmod: u8,
    substream: Substream,
}

impl SyncFrame {
    /// Blocks this frame moves the timeline on. Dependent frames and further programs (`substreamid`
    /// above 0) cover the same time as program 0's independent frames.
    fn timeline_blocks(&self) -> u32 {
        match self.substream {
            Substream::Ac3 | Substream::Independent(0) => self.blocks,
            Substream::Independent(_) | Substream::Dependent => 0,
        }
    }
}

fn parse(b: &[u8]) -> Result<SyncFrame, Error> {
    if !b.starts_with(&SYNC) {
        return Err(if b.len() < SYNC.len() && SYNC.starts_with(b) {
            Error::Truncated
        } else {
            Error::Sync
        });
    }
    if b.len() < HEADER_BYTES {
        return Err(Error::Truncated);
    }
    let bsid = b[5] >> 3;
    let fscod = usize::from(b[4] >> 6);
    match bsid {
        0..=8 => {
            let frmsizecod = usize::from(b[4] & 0x3F);
            if fscod == 3 || frmsizecod > 37 {
                return Err(Error::Header);
            }
            let kbps = AC3_KBPS[frmsizecod / 2];
            // 16-bit words per 1536-sample frame. At 44.1 kHz the size isn't whole, so encoders alternate
            // the rounded-down size and one word more (the low bit of frmsizecod).
            let words = match fscod {
                0 => kbps * 2,
                1 => kbps * 320 / 147 + (frmsizecod & 1),
                _ => kbps * 3,
            };
            Ok(SyncFrame {
                bytes: words * 2,
                sample_rate: SAMPLE_RATES[fscod],
                blocks: BLOCKS_PER_BURST,
                bsmod: b[5] & 0x07,
                substream: Substream::Ac3,
            })
        }
        // AC-3 at half and quarter sample rates: not an IEC 61937-3 format.
        9 | 10 => Err(Error::Unsupported),
        11..=16 => {
            let strmtyp = b[2] >> 6;
            let substream = match strmtyp {
                0 | 2 => Substream::Independent((b[2] >> 3) & 0x07),
                1 => Substream::Dependent,
                _ => return Err(Error::Header),
            };
            let bytes = ((usize::from(b[2] & 0x07) << 8 | usize::from(b[3])) + 1) * 2;
            if bytes < HEADER_BYTES {
                return Err(Error::Header);
            }
            // fscod 3 means the reduced rates (fscod2); IEC 61937-3 carries only 32, 44.1 and 48 kHz.
            if fscod == 3 {
                return Err(if (b[4] >> 4) & 0x03 == 3 {
                    Error::Header
                } else {
                    Error::Unsupported
                });
            }
            Ok(SyncFrame {
                bytes,
                sample_rate: SAMPLE_RATES[fscod],
                blocks: EAC3_BLOCKS[usize::from((b[4] >> 4) & 0x03)],
                bsmod: 0,
                substream,
            })
        }
        _ => Err(Error::Header),
    }
}

/// The sync frames packed back to back in one access unit: one AC-3 frame, or an E-AC-3 independent
/// frame with its dependent frames, unless the demuxer delivered several. Bytes after the last whole
/// frame are dropped; a receiver couldn't use them. An empty result is never returned.
fn sync_frames(unit: &[u8]) -> Result<Vec<(&[u8], SyncFrame)>, Error> {
    let mut frames = Vec::with_capacity(2);
    let mut rest = unit;
    while !rest.is_empty() {
        let frame = match parse(rest) {
            Ok(frame) if frame.bytes <= rest.len() => frame,
            Ok(_) if frames.is_empty() => return Err(Error::Truncated),
            Err(error) if frames.is_empty() => return Err(error),
            _ => break,
        };
        let (bytes, tail) = rest.split_at(frame.bytes);
        frames.push((bytes, frame));
        rest = tail;
    }
    if frames.is_empty() {
        return Err(Error::Truncated);
    }
    Ok(frames)
}

/// AC-3 (Dolby Digital): one burst per sync frame (IEC 61937-3, data type 1), period 1536 frames, so a
/// 48 kHz stream needs a 2-channel 48 kHz carrier.
#[derive(Debug, Default)]
pub struct Ac3Packer {
    carrier: Option<Carrier>,
}

impl Ac3Packer {
    pub fn new() -> Self {
        Self::default()
    }
}

impl Packer for Ac3Packer {
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error> {
        let frames = sync_frames(unit)?;
        if frames
            .iter()
            .any(|(_, frame)| frame.substream != Substream::Ac3)
        {
            return Err(Error::Unsupported);
        }
        let mut bursts = Vec::with_capacity(frames.len());
        for (bytes, frame) in frames {
            let carrier = ac3_carrier(frame.sample_rate)?;
            // Pd counts bits for AC-3. The largest frame (3840 bytes) always fits the period.
            let preamble = Preamble {
                pc: DATA_TYPE_AC3 | u16::from(frame.bsmod) << 8,
                pd: length_code(bytes.len() * 8)?,
            };
            bursts.push(burst(
                carrier,
                Some(preamble),
                bytes,
                Words::BigEndian,
                AC3_PERIOD_BYTES,
            )?);
            self.carrier = Some(carrier);
        }
        Ok(bursts)
    }

    fn flush(&mut self) -> Vec<Burst> {
        Vec::new()
    }

    fn reset(&mut self) {}

    fn carrier(&self) -> Option<Carrier> {
        self.carrier
    }
}

/// E-AC-3 (Dolby Digital Plus, Atmos included: its objects ride inside the frames): sync frames
/// gathered until program 0 has six audio blocks, one burst each (IEC 61937-3, data type 21, Pd in
/// bytes), period 6144 frames at four times the stream rate: 192 kHz, 2 channels for 48 kHz.
///
/// A unit is counted by its program 0 independent frames' blocks, so 1-, 2- and 3-block streams fill a
/// burst with 6, 3 and 2 units, and dependent frames (7.1) and further programs ride along without
/// moving time. FFmpeg's muxer counts units by the first frame's `numblkscod` instead, which gives the
/// same bursts for every stream that keeps one block count.
#[derive(Debug, Default)]
pub struct Eac3Packer {
    pending: Vec<u8>,
    blocks: u32,
    carrier: Option<Carrier>,
}

impl Eac3Packer {
    pub fn new() -> Self {
        Self::default()
    }

    fn take_burst(&mut self) -> Result<Burst, Error> {
        let carrier = self.carrier.ok_or(Error::Header)?;
        let preamble = Preamble {
            pc: DATA_TYPE_EAC3,
            pd: length_code(self.pending.len())?,
        };
        let packed = burst(
            carrier,
            Some(preamble),
            &self.pending,
            Words::BigEndian,
            EAC3_PERIOD_BYTES,
        );
        self.reset();
        packed
    }
}

impl Packer for Eac3Packer {
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error> {
        let frames = sync_frames(unit)?;
        let carrier = eac3_carrier(frames[0].1.sample_rate)?;
        let bytes: usize = frames.iter().map(|(bytes, _)| bytes.len()).sum();
        if PREAMBLE_BYTES + (self.pending.len() + bytes).next_multiple_of(2) > EAC3_PERIOD_BYTES {
            // Over IEC 61937-3's bitrate: the burst being filled can't be sent either.
            self.reset();
            return Err(Error::TooLarge);
        }
        for (bytes, frame) in &frames {
            self.pending.extend_from_slice(bytes);
            self.blocks += frame.timeline_blocks();
        }
        self.carrier = Some(carrier);
        if self.blocks < BLOCKS_PER_BURST {
            return Ok(Vec::new());
        }
        Ok(vec![self.take_burst()?])
    }

    fn flush(&mut self) -> Vec<Burst> {
        if self.pending.is_empty() {
            return Vec::new();
        }
        self.take_burst().into_iter().collect()
    }

    fn reset(&mut self) {
        self.pending.clear();
        self.blocks = 0;
    }

    fn carrier(&self) -> Option<Carrier> {
        self.carrier
    }
}
