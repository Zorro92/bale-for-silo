//! DTS in IEC 61937-5 bursts: the core alone (types I-III) or DTS-HD whole (type IV). Core header fields
//! from ETSI TS 102 114 (5.3.1), the extension substream sync word from its 7.4.

use super::{
    Burst, Carrier, Error, Packer, Preamble, Subtype, Words, base_rate, burst, length_code,
};

/// How the core frame is stored (ETSI TS 102 114, 5.2): 16-bit or 14-bit words (14-bit is the CD/WAV
/// form, two sign bits in every word), each big- or little-endian.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Packing {
    Be16,
    Le16,
    Be14,
    Le14,
}

impl Packing {
    fn words(self) -> Words {
        match self {
            Self::Be16 | Self::Be14 => Words::BigEndian,
            Self::Le16 | Self::Le14 => Words::LittleEndian,
        }
    }
}

const SYNC_BE16: [u8; 4] = [0x7F, 0xFE, 0x80, 0x01];
const SYNC_LE16: [u8; 4] = [0xFE, 0x7F, 0x01, 0x80];
const SYNC_BE14: [u8; 4] = [0x1F, 0xFF, 0xE8, 0x00];
const SYNC_LE14: [u8; 4] = [0xFF, 0x1F, 0x00, 0xE8];
/// The DTS-HD extension substream (`SYNCEXTSSH`), which follows the core in DTS-HD streams.
const SYNC_EXSS: [u8; 4] = [0x64, 0x58, 0x20, 0x25];

/// `SFREQ` codes.
const SAMPLE_RATES: [u32; 16] = [
    0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 0, 0,
];
/// The core header bytes read, in 16-bit big-endian form: sync, `FTYPE`..`NBLKS`, `FSIZE`, `AMODE`, `SFREQ`.
const HEADER_BYTES: usize = 9;
/// 14-bit words that hold those bytes' 72 bits.
const HEADER_WORDS_14: usize = 6;
/// The smallest legal `FSIZE + 1`.
const MIN_FRAME_BYTES: usize = 96;

/// The DTS-HD burst header in front of the core (IEC 61937-5 type IV), followed by the payload's
/// length as a big-endian 16-bit word.
const HD_HEADER: [u8; 10] = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFE, 0xFE];
const DATA_TYPE_TYPE_IV: u16 = 17;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct Core {
    packing: Packing,
    /// PCM samples per channel in the frame: `(NBLKS + 1) * 32`.
    pub(super) samples: usize,
    pub(super) sample_rate: u32,
    /// `FSIZE + 1`, the core frame's length in its 16-bit form.
    pub(super) frame_bytes: usize,
}

impl Core {
    pub(super) fn parse(unit: &[u8]) -> Result<Self, Error> {
        let packing = match unit.get(..4) {
            Some(sync) if sync == SYNC_BE16 => Packing::Be16,
            Some(sync) if sync == SYNC_LE16 => Packing::Le16,
            Some(sync) if sync == SYNC_BE14 => Packing::Be14,
            Some(sync) if sync == SYNC_LE14 => Packing::Le14,
            Some(sync) if sync == SYNC_EXSS => return Err(Error::MissingCore),
            Some(_) => return Err(Error::Sync),
            None => return Err(Error::Truncated),
        };
        let h = header(unit, packing).ok_or(Error::Truncated)?;
        let samples = ((usize::from(h[4] & 0x01) << 6 | usize::from(h[5] >> 2)) + 1) * 32;
        let frame_bytes =
            (usize::from(h[5] & 0x03) << 12 | usize::from(h[6]) << 4 | usize::from(h[7] >> 4)) + 1;
        let sample_rate = SAMPLE_RATES[usize::from((h[8] >> 2) & 0x0F)];
        if frame_bytes < MIN_FRAME_BYTES || sample_rate == 0 {
            return Err(Error::Header);
        }
        Ok(Self {
            packing,
            samples,
            sample_rate,
            frame_bytes,
        })
    }
}

/// The first header bytes as the 16-bit big-endian bitstream, whatever the packing.
fn header(unit: &[u8], packing: Packing) -> Option<[u8; HEADER_BYTES]> {
    let mut h = [0; HEADER_BYTES];
    match packing {
        Packing::Be16 => h.copy_from_slice(unit.get(..HEADER_BYTES)?),
        Packing::Le16 => {
            // Words, so an odd count reads one byte more.
            let words = unit.get(..HEADER_BYTES + 1)?;
            for (i, byte) in h.iter_mut().enumerate() {
                *byte = words[i ^ 1];
            }
        }
        Packing::Be14 | Packing::Le14 => {
            let words = unit.get(..HEADER_WORDS_14 * 2)?;
            let mut bits: u128 = 0;
            for &word in words.as_chunks::<2>().0 {
                let word = if packing == Packing::Be14 {
                    u16::from_be_bytes(word)
                } else {
                    u16::from_le_bytes(word)
                };
                bits = bits << 14 | u128::from(word & 0x3FFF);
            }
            // 84 bits gathered; the header is the first 72.
            let bytes = (bits >> (HEADER_WORDS_14 * 14 - HEADER_BYTES * 8)).to_be_bytes();
            h.copy_from_slice(&bytes[bytes.len() - HEADER_BYTES..]);
        }
    }
    Some(h)
}

pub(super) fn core_carrier(sample_rate: u32) -> Result<Carrier, Error> {
    Ok(Carrier::new(Subtype::Dts, 2, base_rate(sample_rate)?))
}

/// DTS core (DTS Digital Surround): one burst per core frame (IEC 61937-5 types I, II and III for 512,
/// 1024 and 2048 samples, Pd in bits), period one IEC 60958 frame per sample, 2 channels at the core
/// rate. A DTS-HD stream's extension substream after the core is dropped, so any DTS receiver can play
/// the core. 14-bit and little-endian streams are carried as they are, like FFmpeg (no trimming), and a
/// frame that fills its period exactly (DTS CD and WAV rips) goes without a preamble.
#[derive(Debug, Default)]
pub struct DtsPacker {
    carrier: Option<Carrier>,
}

impl DtsPacker {
    pub fn new() -> Self {
        Self::default()
    }
}

impl Packer for DtsPacker {
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error> {
        let core = Core::parse(unit)?;
        let data_type = match core.samples {
            512 => 11,
            1024 => 12,
            2048 => 13,
            _ => return Err(Error::Unsupported),
        };
        let carrier = core_carrier(core.sample_rate)?;
        // Only 16-bit big-endian frames carry extension substreams (DTS-HD) and have their size in
        // stream bytes. Pd is the trimmed core's exact bit count, or the whole unit's rounded up to
        // whole words (FFmpeg's two cases).
        let (payload, pd_bytes) = match core.packing {
            Packing::Be16 if unit.len() < core.frame_bytes => return Err(Error::Truncated),
            Packing::Be16 if unit.len() > core.frame_bytes => {
                (&unit[..core.frame_bytes], core.frame_bytes)
            }
            _ => (unit, unit.len().next_multiple_of(2)),
        };
        let period_bytes = core.samples * 4;
        let preamble = if payload.len() == period_bytes {
            None
        } else {
            Some(Preamble {
                pc: data_type,
                pd: length_code(pd_bytes * 8)?,
            })
        };
        let packed = burst(
            carrier,
            preamble,
            payload,
            core.packing.words(),
            period_bytes,
        )?;
        self.carrier = Some(carrier);
        Ok(vec![packed])
    }

    fn flush(&mut self) -> Vec<Burst> {
        Vec::new()
    }

    fn reset(&mut self) {}

    fn carrier(&self) -> Option<Carrier> {
        self.carrier
    }
}

/// The link a DTS-HD burst stream needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DtsHdLink {
    /// HDMI high-bitrate audio: 8 channels at four times the core rate (192 kHz for 48 kHz), 16 IEC
    /// 60958 frames per core sample. Room for any DTS-HD Master Audio stream.
    #[default]
    HighBitrate,
    /// 2 channels at four times the core rate: a quarter of the room, enough for High Resolution Audio
    /// but not always for Master Audio (frames that don't fit fall back to the core).
    Stereo,
}

impl DtsHdLink {
    /// IEC 60958 frames (4 bytes) per core sample: FFmpeg's `dtshd_rate` over the core rate.
    fn frames_per_sample(self) -> usize {
        match self {
            Self::HighBitrate => 16,
            Self::Stereo => 4,
        }
    }
}

pub(super) fn hd_carrier(link: DtsHdLink, sample_rate: u32) -> Result<Carrier, Error> {
    let channels = match link {
        DtsHdLink::HighBitrate => 8,
        DtsHdLink::Stereo => 2,
    };
    Ok(Carrier::new(
        Subtype::DtsHd,
        channels,
        base_rate(sample_rate)? * 4,
    ))
}

/// Seconds a DTS-HD stream that overflowed its burst stays core-only, so it doesn't flip between DTS-HD
/// and core on every peak (FFmpeg's `dtshd_fallback_time` default).
const FALLBACK_SECONDS: usize = 60;

/// DTS-HD Master Audio / High Resolution Audio: each core frame with its extension substream, behind
/// the 12-byte DTS-HD header, in an IEC 61937-5 type IV burst (Pd in bytes). The period follows the
/// link: 16 IEC 60958 frames per core sample over HBR (2048 8-channel frames at 192 kHz for 512 samples,
/// repetition subtype 4), 4 over the stereo link. A frame too large for its period starts a minute of
/// core-only bursts, still type IV.
///
/// FFmpeg pads Pd so `Pd % 16 == 8`, reportedly for some receivers; that is kept. Only 16-bit
/// big-endian cores carry DTS-HD.
#[derive(Debug)]
pub struct DtsHdPacker {
    link: DtsHdLink,
    core_only: usize,
    carrier: Option<Carrier>,
}

impl DtsHdPacker {
    pub fn new(link: DtsHdLink) -> Self {
        Self {
            link,
            core_only: 0,
            carrier: None,
        }
    }
}

impl Packer for DtsHdPacker {
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error> {
        let core = Core::parse(unit)?;
        if core.packing != Packing::Be16 {
            return Err(Error::Unsupported);
        }
        if unit.len() < core.frame_bytes {
            return Err(Error::Truncated);
        }
        let carrier = hd_carrier(self.link, core.sample_rate)?;
        let period_frames = self.link.frames_per_sample() * core.samples;
        // The repetition period as IEC 61937-5 type IV's subtype (Pc bits 8..10): 512 << subtype frames.
        let subtype = match period_frames {
            512 => 0,
            1024 => 1,
            2048 => 2,
            4096 => 3,
            8192 => 4,
            16384 => 5,
            _ => return Err(Error::Unsupported),
        };
        let period_bytes = period_frames * 4;
        let mut core_only = self.core_only;
        if HD_HEADER.len() + 2 + unit.len() > period_bytes - super::PREAMBLE_BYTES {
            core_only = core.sample_rate as usize * FALLBACK_SECONDS / core.samples;
        }
        let body = if core_only > 0 {
            core_only -= 1;
            &unit[..core.frame_bytes]
        } else {
            unit
        };
        let mut payload = Vec::with_capacity(HD_HEADER.len() + 2 + body.len());
        payload.extend_from_slice(&HD_HEADER);
        payload.extend_from_slice(&length_code(body.len())?.to_be_bytes());
        payload.extend_from_slice(body);
        let preamble = Preamble {
            pc: DATA_TYPE_TYPE_IV | subtype << 8,
            pd: length_code((payload.len() + 8).next_multiple_of(16) - 8)?,
        };
        let packed = burst(
            carrier,
            Some(preamble),
            &payload,
            Words::BigEndian,
            period_bytes,
        )?;
        self.core_only = core_only;
        self.carrier = Some(carrier);
        Ok(vec![packed])
    }

    fn flush(&mut self) -> Vec<Burst> {
        Vec::new()
    }

    /// The core-only fallback is about the stream's bitrate, not its position, so it survives a seek.
    fn reset(&mut self) {}

    fn carrier(&self) -> Option<Carrier> {
        self.carrier
    }
}
