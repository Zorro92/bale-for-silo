//! IEC 61937 packers: compressed audio wrapped in a PCM-shaped bitstream for passthrough over S/PDIF or
//! HDMI to a receiver that decodes it (docs/audio-passthrough.md, "IEC 61937 packers").
//!
//! A receiver sees what looks like 16-bit PCM. IEC 61937-1 hides compressed frames in it as data bursts:
//! a preamble of four 16-bit words (Pa = 0xF872 and Pb = 0x4E1F to sync on, Pc = the data type, Pd = the
//! payload length), the payload, then zero stuffing up to the format's repetition period. The period is
//! exactly the time the burst's audio plays for, so the carrier keeps a fixed sample clock whatever the
//! codec's bitrate, and counting carrier frames is the clock: a [`Burst`] of `frames` carrier frames
//! plays for `frames / carrier.sample_rate` seconds, and the first burst after [`Packer::reset`] starts
//! at the first access unit pushed after it.
//!
//! | Format | Standard | Burst (Pc data type) | Period | Carrier ([`Carrier`]) |
//! |---|---|---|---|---|
//! | AC-3 | IEC 61937-3 | 1: one sync frame | 1536 frames | 2 ch at the stream rate |
//! | E-AC-3 | IEC 61937-3 | 21: sync frames for six audio blocks | 6144 frames | 2 ch at 4× the stream rate |
//! | DTS | IEC 61937-5 | 11/12/13: one core frame of 512/1024/2048 samples | 512/1024/2048 frames | 2 ch at the core rate |
//! | DTS-HD MA/HRA | IEC 61937-5 | 17 (type IV): DTS-HD header, core and extension substream | 4× the core's samples | 8 ch (HBR) at 4× the core rate |
//! | TrueHD, MLP | IEC 61937-9 | 22: one MAT frame of 24 access units (1/50 s) | 3840 frames | 8 ch (HBR) at 192 kHz (176.4 kHz for 44.1 kHz streams) |
//!
//! Output bytes are what a WASAPI exclusive-mode client writes for a 16-bit `WAVEFORMATEXTENSIBLE_IEC61937`
//! with the carrier's channels, rate and [`Subtype`]: little-endian 16-bit samples, interleaved. Payloads
//! are taken as big-endian 16-bit words (the bitstream's first byte is the word's high byte), as IEC 61937
//! sends them, so each byte pair is swapped; a final odd byte becomes the high byte of a last word.
//!
//! Each packer takes complete elementary-stream access units as FFmpeg's demuxers deliver them (one AC-3
//! frame, one E-AC-3 independent frame with its dependent frames, one DTS core frame with its extension
//! substream, one TrueHD access unit) and returns the bursts they complete. A unit that holds several
//! frames is split. A unit the packer rejects returns an [`Error`] and changes nothing, so the caller can
//! skip it and go on.
//!
//! Written from the standards and public format descriptions: IEC 61937-1/-3/-5/-9, ATSC A/52 (AC-3 and
//! E-AC-3 headers), ETSI TS 102 114 (DTS core header and extension substream), the MLP/TrueHD access-unit
//! header, and Microsoft's "Representing Formats for IEC 61937 Transmissions" for the subtypes. FFmpeg's
//! `libavformat/spdifenc.c` and VLC's `modules/audio_filter/converter/tospdif.c` (both LGPL) were read as
//! references for the byte layouts only; no code was taken from them. Where the standards leave a choice
//! open (Pd rounding, DTS-HD length alignment, TrueHD padding, MAT code positions), FFmpeg's choice is
//! followed so the output is byte-identical to its `spdif` muxer, which `tests/iec61937_golden.rs` checks
//! against fixtures made with it (`tests/fixtures/iec61937/README.md`).

mod ac3;
mod dts;
mod mat;

pub use ac3::{Ac3Packer, Eac3Packer};
pub use dts::{DtsHdLink, DtsHdPacker, DtsPacker};
pub use mat::TrueHdPacker;

/// Burst preamble sync words Pa and Pb (IEC 61937-1).
const PA: u16 = 0xF872;
const PB: u16 = 0x4E1F;
/// Pa, Pb, Pc and Pd.
const PREAMBLE_BYTES: usize = 8;

/// The Windows `KSDATAFORMAT_SUBTYPE_IEC61937_*` subtype (ksmedia.h) a `WAVEFORMATEXTENSIBLE_IEC61937`
/// declares for the bursts.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Subtype {
    DolbyDigital,
    DolbyDigitalPlus,
    Dts,
    DtsHd,
    /// Dolby TrueHD and MLP, both in MAT frames.
    DolbyMlp,
}

impl Subtype {
    /// The ksmedia.h name.
    pub const fn name(self) -> &'static str {
        match self {
            Self::DolbyDigital => "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL",
            Self::DolbyDigitalPlus => "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS",
            Self::Dts => "KSDATAFORMAT_SUBTYPE_IEC61937_DTS",
            Self::DtsHd => "KSDATAFORMAT_SUBTYPE_IEC61937_DTS_HD",
            Self::DolbyMlp => "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_MLP",
        }
    }

    /// The GUID, as `windows_core::GUID::from_u128` takes it (the values the `windows` crate defines).
    pub const fn guid(self) -> u128 {
        match self {
            Self::DolbyDigital => 0x00000092_0000_0010_8000_00aa00389b71,
            Self::DolbyDigitalPlus => 0x0000000a_0cea_0010_8000_00aa00389b71,
            Self::Dts => 0x00000008_0000_0010_8000_00aa00389b71,
            Self::DtsHd => 0x0000000b_0cea_0010_8000_00aa00389b71,
            Self::DolbyMlp => 0x0000000c_0cea_0010_8000_00aa00389b71,
        }
    }
}

/// The PCM-shaped format the bursts travel in: what the audio client opens the device with.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Carrier {
    pub subtype: Subtype,
    pub channels: u16,
    pub sample_rate: u32,
    /// Always 16: IEC 61937 is defined on 16-bit IEC 60958 subframes.
    pub bits_per_sample: u16,
}

impl Carrier {
    const fn new(subtype: Subtype, channels: u16, sample_rate: u32) -> Self {
        Self {
            subtype,
            channels,
            sample_rate,
            bits_per_sample: 16,
        }
    }

    /// Bytes per carrier frame (one sample for every channel): `nBlockAlign`.
    pub const fn block_align(&self) -> usize {
        self.channels as usize * 2
    }

    /// `nAvgBytesPerSec`.
    pub const fn bytes_per_second(&self) -> u32 {
        self.sample_rate * self.channels as u32 * 2
    }
}

/// Sample rates IEC 61937 carries a stream at (IEC 60958's 32, 44.1 and 48 kHz base rates); the
/// carriers run at these or at four times these.
fn base_rate(sample_rate: u32) -> Result<u32, Error> {
    match sample_rate {
        32_000 | 44_100 | 48_000 => Ok(sample_rate),
        _ => Err(Error::Unsupported),
    }
}

/// One data burst and its stuffing: a whole repetition period, ready to write.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Burst {
    /// Little-endian 16-bit samples, interleaved over the carrier's channels:
    /// `frames * carrier.block_align()` bytes.
    pub data: Vec<u8>,
    /// Carrier frames the burst fills: its duration at the carrier rate.
    pub frames: u32,
    pub carrier: Carrier,
}

impl Burst {
    /// Playing time, `frames / carrier.sample_rate`.
    pub fn seconds(&self) -> f64 {
        f64::from(self.frames) / f64::from(self.carrier.sample_rate)
    }
}

/// Why an access unit was rejected. Nothing was packed from it and the packer's state is unchanged
/// (except [`Error::TooLarge`] on E-AC-3, which also drops the burst it was filling).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Error {
    /// The unit doesn't start with the format's sync word.
    Sync,
    /// A header field holds a reserved or impossible value.
    Header,
    /// The unit is shorter than its header says.
    Truncated,
    /// A valid unit IEC 61937 (or this packer) doesn't carry: an unsupported frame length, sample rate,
    /// packing or substream.
    Unsupported,
    /// The unit doesn't fit its burst's repetition period: the bitrate is too high for the carrier.
    TooLarge,
    /// A DTS-HD extension substream with no core frame in front of it. DTS-HD streams with a core
    /// sometimes start with one; skip it.
    MissingCore,
    /// TrueHD/MLP access units before the first major sync: their duration is still unknown.
    AwaitingMajorSync,
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Sync => "no sync word at the start of the access unit",
            Self::Header => "invalid header field",
            Self::Truncated => "access unit shorter than its header says",
            Self::Unsupported => "stream variant IEC 61937 packing does not support",
            Self::TooLarge => "access unit too large for the burst period",
            Self::MissingCore => "DTS-HD extension substream without a core frame",
            Self::AwaitingMajorSync => "TrueHD/MLP access unit before the first major sync",
        })
    }
}

impl std::error::Error for Error {}

/// One format's packer. Feed it access units in decode order.
pub trait Packer {
    /// Packs one access unit (or several back to back) and returns the bursts it completed, in order.
    fn push(&mut self, unit: &[u8]) -> Result<Vec<Burst>, Error>;

    /// End of stream: the burst still being filled (E-AC-3 blocks, a MAT frame), completed with
    /// stuffing, then [`Packer::reset`]. FFmpeg's muxer drops this partial burst instead.
    fn flush(&mut self) -> Vec<Burst>;

    /// Drops what is being filled and the timing history, for a seek or a discontinuity; the next unit
    /// starts a new burst. Facts about the stream (TrueHD's rate, a DTS-HD core-only fallback) are kept.
    fn reset(&mut self);

    /// The carrier of the last burst, once a unit has shown the stream's rate. [`Format::carrier`] gives
    /// it from the demuxer's sample rate before the first unit.
    fn carrier(&self) -> Option<Carrier>;
}

/// The formats there are packers for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Format {
    Ac3,
    Eac3,
    /// DTS core only: a DTS-HD stream's extension substream is dropped.
    Dts,
    /// DTS-HD Master Audio / High Resolution Audio, over HDMI high-bitrate audio.
    DtsHd,
    /// Dolby TrueHD and MLP.
    TrueHd,
}

impl Format {
    /// The carrier for a stream whose codec runs at `sample_rate` (the demuxer's rate; for DTS-HD the
    /// core's 48 or 44.1 kHz, for TrueHD any rate of the 48 or 44.1 kHz family), if IEC 61937 carries it.
    pub fn carrier(self, sample_rate: u32) -> Option<Carrier> {
        match self {
            Self::Ac3 => ac3::ac3_carrier(sample_rate),
            Self::Eac3 => ac3::eac3_carrier(sample_rate),
            Self::Dts => dts::core_carrier(sample_rate),
            Self::DtsHd => dts::hd_carrier(DtsHdLink::default(), sample_rate),
            Self::TrueHd => mat::carrier(sample_rate),
        }
        .ok()
    }

    /// A packer with the default settings (DTS-HD over HBR).
    pub fn packer(self) -> Box<dyn Packer + Send> {
        match self {
            Self::Ac3 => Box::new(Ac3Packer::new()),
            Self::Eac3 => Box::new(Eac3Packer::new()),
            Self::Dts => Box::new(DtsPacker::new()),
            Self::DtsHd => Box::new(DtsHdPacker::new(DtsHdLink::default())),
            Self::TrueHd => Box::new(TrueHdPacker::new()),
        }
    }
}

/// Burst info and length code.
#[derive(Debug, Clone, Copy)]
struct Preamble {
    /// Pc: data type in bits 0..4, data-type-dependent info in bits 8..12.
    pc: u16,
    /// Pd: the payload length, in bits or bytes depending on the data type.
    pd: u16,
}

/// How the payload's bytes sit in the stream it came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Words {
    /// Bitstream order, as IEC 61937 sends it: big-endian words, swapped to little-endian samples.
    BigEndian,
    /// Already little-endian 16-bit words (DTS's little-endian packings): copied as they are.
    LittleEndian,
}

/// A length code that must fit Pd's 16 bits.
fn length_code(value: usize) -> Result<u16, Error> {
    u16::try_from(value).map_err(|_| Error::TooLarge)
}

/// Writes one repetition period: the preamble (none only for a DTS frame that fills its period
/// exactly), the payload and zero stuffing.
fn burst(
    carrier: Carrier,
    preamble: Option<Preamble>,
    payload: &[u8],
    words: Words,
    period_bytes: usize,
) -> Result<Burst, Error> {
    let header = if preamble.is_some() {
        PREAMBLE_BYTES
    } else {
        0
    };
    // A final odd byte still takes a whole word.
    if header + payload.len().next_multiple_of(2) > period_bytes {
        return Err(Error::TooLarge);
    }
    Ok(write_burst(carrier, preamble, payload, words, period_bytes))
}

/// [`burst`] for a payload known to fit.
fn write_burst(
    carrier: Carrier,
    preamble: Option<Preamble>,
    payload: &[u8],
    words: Words,
    period_bytes: usize,
) -> Burst {
    let mut data = Vec::with_capacity(period_bytes);
    if let Some(Preamble { pc, pd }) = preamble {
        for word in [PA, PB, pc, pd] {
            data.extend_from_slice(&word.to_le_bytes());
        }
    }
    let (pairs, odd) = payload.as_chunks::<2>();
    match words {
        Words::BigEndian => pairs
            .iter()
            .for_each(|&[high, low]| data.extend_from_slice(&[low, high])),
        Words::LittleEndian => data.extend_from_slice(pairs.as_flattened()),
    }
    if let [last] = odd {
        data.extend_from_slice(&[0, *last]);
    }
    data.resize(period_bytes, 0);
    // Periods are at most 65536 bytes.
    let frames = (period_bytes / carrier.block_align()) as u32;
    Burst {
        data,
        frames,
        carrier,
    }
}

#[cfg(test)]
mod tests;
