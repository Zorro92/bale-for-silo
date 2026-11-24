//! Exclusive-mode WASAPI audio output (docs/audio-passthrough.md, "Exclusive output"): debug plumbing for
//! passthrough and decoded multichannel PCM, C ABI 1 in `include/bale_audio_out.h` (`bale_audio_out_*`).
//! Nothing in a release path creates an output yet.
//!
//! One output is one exclusive-mode `IAudioClient` on the default render endpoint, activated like the
//! capability probe (`MediaDevice` default render id, then `ActivateAudioInterfaceAsync` from the caller's
//! `mmdevapi.dll`, which bale_gpu never imports), with `AudioCategory_Movie`, event-driven with a buffer of one
//! device period. A render thread owns every COM call: it fills the device buffer from a bounded [`ring`] of
//! queued frames each period, plays silence when the ring runs dry (counted), keeps a [`timeline`] of which
//! pts each device frame carries, and slaves the audio to the caller's video clock ([`sync`]) by dropping or
//! padding whole frames. Outputs are named by small ids ([`table`]) so an id fits the 32-bit FFmpegOptions
//! value the FFmpegInteropX fork receives.
//!
//! This file holds the ABI types, the format rules and the `WAVEFORMATEXTENSIBLE` the output opens; the
//! submodules are platform independent and unit tested on Linux except `wasapi` (Windows: the render thread
//! and the exports).
//!
//! Sized for docs/resource-budget.md's audio line (< 0.25 core, ~20 MiB) inside the measured console limits
//! (3328 MiB app memory, 4 shared cores, CPU copies 3.6-4x slower when input-idle): the ring holds at most
//! 1000 ms (default 500 ms: 750 KiB for 8 ch 32-bit 48 kHz PCM, 1.46 MiB for an 8-line 192 kHz IEC 61937
//! link), the device buffer is the audio engine's, and the one render thread wakes once a period (10 ms by
//! default) to copy one period (15 KiB for 7.1 PCM): ~1.5 MB/s of copies, far below a core even idle. No GPU.

pub mod render;
pub mod ring;
pub mod sync;
pub mod table;
pub mod timeline;
pub mod variants;
#[cfg(windows)]
mod wasapi;

#[cfg(windows)]
pub use variants::bale_audio_out_init_variants;
#[cfg(windows)]
pub use wasapi::{
    bale_audio_out_create, bale_audio_out_destroy, bale_audio_out_flush, bale_audio_out_format,
    bale_audio_out_pause, bale_audio_out_start, bale_audio_out_stats, bale_audio_out_sync,
    bale_audio_out_write,
};

use crate::audio_probe::{
    Guid, SPEAKER_7POINT1_SURROUND, SPEAKER_STEREO, SUBTYPE_IEC61937_DOLBY_DIGITAL,
    SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS, SUBTYPE_IEC61937_DOLBY_MLP, SUBTYPE_IEC61937_DTS,
    SUBTYPE_IEC61937_DTS_HD, SUBTYPE_PCM, WaveFormat,
};

/// Returned by `bale_audio_out_abi_version`; bumped whenever a `bale_audio_out_*` export changes.
pub const ABI_VERSION: u32 = 1;

/// `BaleAudioFormat::kind`.
pub const KIND_PCM: u32 = 0;
pub const KIND_IEC61937: u32 = 1;

/// `BaleAudioFormat::iec_subtype`: the `KSDATAFORMAT_SUBTYPE_IEC61937_*` the link declares.
pub const IEC_NONE: u32 = 0;
pub const IEC_AC3: u32 = 1;
pub const IEC_EAC3: u32 = 2;
pub const IEC_DTS: u32 = 3;
pub const IEC_DTS_HD: u32 = 4;
pub const IEC_MLP: u32 = 5;

/// `pts_100ns` of a write that continues the previous one (no new timestamp).
pub const PTS_NONE: i64 = i64::MIN;
/// `bale_audio_out_write`'s answer for an unknown id or an output that can no longer play (device lost,
/// failed, being destroyed): stop feeding it.
pub const WRITE_CLOSED: u32 = u32::MAX;

/// `BaleAudioOutStats::status`.
pub const STATUS_STOPPED: u32 = 0;
pub const STATUS_RUNNING: u32 = 1;
pub const STATUS_PAUSED: u32 = 2;
pub const STATUS_DEVICE_LOST: u32 = 3;
pub const STATUS_FAILED: u32 = 4;

/// `BaleAudioOutOpenInfo::step`: where `bale_audio_out_create` stopped.
pub const STEP_DONE: u32 = 0;
pub const STEP_ARGUMENTS: u32 = 1;
pub const STEP_MODULE: u32 = 2;
pub const STEP_THREAD: u32 = 3;
pub const STEP_DEVICE_ID: u32 = 4;
pub const STEP_ACTIVATE: u32 = 5;
pub const STEP_FORMAT: u32 = 6;
pub const STEP_INITIALIZE: u32 = 7;
pub const STEP_SERVICES: u32 = 8;
pub const STEP_TABLE: u32 = 9;

/// `BaleAudioOutOpenInfo::path` (and `BaleAudioOutStats::path`): how the stream was initialized. Exclusive comes
/// first; the offload paths only after the plain exclusive `Initialize` answered `AUDCLNT_E_UNSUPPORTED_FORMAT` or
/// `AUDCLNT_E_OFFLOAD_MODE_ONLY` (the console, 2026-11-24). Shared offload is PCM only: the engine may mix it.
pub const PATH_EXCLUSIVE: u32 = 0;
pub const PATH_EXCLUSIVE_OFFLOAD: u32 = 1;
pub const PATH_SHARED_OFFLOAD: u32 = 2;

/// `BaleAudioOutOpenInfo::mmcss`.
pub const MMCSS_NONE: u32 = 0;
pub const MMCSS_PRO_AUDIO: u32 = 1;
pub const MMCSS_FAILED: u32 = 2;

/// Ring capacity: `ring_ms` 0 means the default; anything else must be in range. Always at least two device
/// buffers.
pub const DEFAULT_RING_MS: u32 = 500;
pub const MIN_RING_MS: u32 = 50;
pub const MAX_RING_MS: u32 = 1000;

pub const S_OK: i32 = 0;
pub const E_POINTER: i32 = 0x8000_4003u32 as i32;
pub const E_UNEXPECTED: i32 = 0x8000_FFFFu32 as i32;
/// `HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE)`: an id that names no live output.
pub const E_HANDLE: i32 = 0x8007_0006u32 as i32;
pub const E_OUTOFMEMORY: i32 = 0x8007_000Eu32 as i32;
pub const E_INVALIDARG: i32 = 0x8007_0057u32 as i32;
pub use crate::audio_probe::{
    AUDCLNT_E_UNSUPPORTED_FORMAT, E_NOT_SET, E_PROC_NOT_FOUND, E_TIMEOUT,
};
pub const AUDCLNT_E_DEVICE_INVALIDATED: i32 = 0x8889_0004u32 as i32;
pub const AUDCLNT_E_DEVICE_IN_USE: i32 = 0x8889_000Au32 as i32;
pub const AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED: i32 = 0x8889_000Eu32 as i32;
pub const AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED: i32 = 0x8889_0019u32 as i32;
pub const AUDCLNT_E_OFFLOAD_MODE_ONLY: i32 = 0x8889_0024u32 as i32;
pub const AUDCLNT_E_RESOURCES_INVALIDATED: i32 = 0x8889_0026u32 as i32;

/// The endpoint went away or changed under the stream (HDMI change, a headset taking over): the caller falls
/// back to its decoded path.
pub fn is_device_lost(hr: i32) -> bool {
    matches!(
        hr,
        AUDCLNT_E_DEVICE_INVALIDATED | AUDCLNT_E_RESOURCES_INVALIDATED
    )
}

/// `KSAUDIO_SPEAKER_MONO` (front centre).
pub const SPEAKER_MONO: u32 = 0x4;
/// Speaker bits ksmedia.h defines (`SPEAKER_FRONT_LEFT` 0x1 to `SPEAKER_TOP_BACK_RIGHT` 0x20000).
const SPEAKER_BITS: u32 = 0x3_FFFF;

/// Carrier rates an IEC 61937 link runs at: IEC 60958's 32, 44.1 and 48 kHz, or four times them.
pub const IEC_RATES: [u32; 6] = [32_000, 44_100, 48_000, 128_000, 176_400, 192_000];

/// The only exclusive format the console's TV path accepted (docs/audio-passthrough.md, probe run of
/// 2026-11-24 with "Allow passthrough" on and no headset): 7.1 PCM at 48 kHz, 24 valid bits in 32.
pub const CONSOLE_PCM_7_1: BaleAudioFormat = BaleAudioFormat {
    kind: KIND_PCM,
    channels: 8,
    sample_rate: 48_000,
    container_bits: 32,
    valid_bits: 24,
    channel_mask: SPEAKER_7POINT1_SURROUND,
    iec_subtype: IEC_NONE,
    encoded_rate: 0,
    encoded_channels: 0,
    reserved: [0; 3],
};

/// `BaleAudioFormat` (48 bytes): what the caller asks the endpoint to open.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleAudioFormat {
    /// [`KIND_PCM`] or [`KIND_IEC61937`].
    pub kind: u32,
    /// PCM 1-8; IEC 61937 2 (one IEC 60958 line) or 8 (high bit rate).
    pub channels: u32,
    /// PCM 8000-384000 Hz; IEC 61937: the carrier rate, one of [`IEC_RATES`].
    pub sample_rate: u32,
    /// PCM 16, 24 or 32; IEC 61937 16 (0 = 16).
    pub container_bits: u32,
    /// 8 to `container_bits`; 0 = `container_bits`. IEC 61937 16.
    pub valid_bits: u32,
    /// ksmedia.h speaker mask, one bit per speaker; 0 = the default for 1, 2 or 8 speakers.
    pub channel_mask: u32,
    /// `IEC_*`: 0 for PCM, 1-5 for IEC 61937.
    pub iec_subtype: u32,
    /// IEC 61937 only: the decoded content's rate. Nonzero opens a `WAVEFORMATEXTENSIBLE_IEC61937` (`cbSize`
    /// 34), 0 a plain `WAVEFORMATEXTENSIBLE` (`cbSize` 22).
    pub encoded_rate: u32,
    /// With `encoded_rate`: the decoded content's channel count (0 = `channels`); the mask then names the
    /// decoded speakers, as in Microsoft's examples. Otherwise 0.
    pub encoded_channels: u32,
    /// Zero.
    pub reserved: [u32; 3],
}

/// `BaleAudioOutOpenInfo` (80 bytes): what `bale_audio_out_create` did, also when it failed. Fields of
/// steps after `step` stay zero.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleAudioOutOpenInfo {
    /// `STEP_*`: where create stopped; [`STEP_DONE`] on success.
    pub step: u32,
    /// Create's return value.
    pub hr: i32,
    /// `ActivateAudioInterfaceAsync`'s result (including its 2 s wait).
    pub activate_hr: i32,
    /// `IAudioClient2::SetClientProperties(AudioCategory_Movie)`; a failure is not fatal.
    pub properties_hr: i32,
    /// `IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE)`.
    pub format_hr: i32,
    /// The first `Initialize`.
    pub initialize_hr: i32,
    /// `Initialize` again with the aligned buffer, when `realigned`.
    pub realign_hr: i32,
    /// How long the (first) activation took.
    pub activate_ms: u32,
    /// `GetDevicePeriod`.
    pub default_period_100ns: i64,
    pub minimum_period_100ns: i64,
    /// The buffer duration and periodicity `Initialize` took (event-driven exclusive: equal).
    pub period_100ns: i64,
    /// `GetBufferSize`: frames the device takes each period.
    pub buffer_frames: u32,
    /// Ring capacity in frames.
    pub ring_frames: u32,
    /// `MMCSS_*`: whether the render thread runs in MMCSS "Pro Audio".
    pub mmcss: u32,
    /// 1 when the first `Initialize` returned `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` and a new client was
    /// initialized with the aligned buffer (Microsoft's documented recipe).
    pub realigned: u32,
    /// `PATH_*`: the path that opened the stream, or the last one tried (was reserved, zero, before 2026-11-24).
    pub path: u32,
    /// `Initialize` of the exclusive offload try; 0 when it was not tried (was reserved).
    pub offload_hr: i32,
}

/// `BaleAudioOutStats` (152 bytes): a snapshot taken without waiting for the render thread.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleAudioOutStats {
    /// `STATUS_*`.
    pub status: u32,
    /// The HRESULT that ended the stream (device lost or failed), else `S_OK`.
    pub error: i32,
    /// Frames the device takes each period.
    pub buffer_frames: u32,
    /// Ring capacity in frames.
    pub ring_frames: u32,
    /// Frames queued in the ring now, and the same in milliseconds (rounded down).
    pub queued_frames: u32,
    pub queued_ms: u32,
    /// Device periods not filled entirely from the ring while playing (silence filled the rest).
    pub underruns: u32,
    /// `bale_audio_out_flush` calls.
    pub flushes: u32,
    /// Frames `bale_audio_out_write` accepted since create.
    pub frames_written: u64,
    /// Frames handed to the device since create: queued data, correction padding and underrun silence.
    pub frames_rendered: u64,
    /// Underrun silence frames.
    pub silence_frames: u64,
    /// Device periods served.
    pub events: u64,
    /// `IAudioClock` position since create or the last flush (which resets it), in 100 ns.
    pub device_position_100ns: i64,
    /// QueryPerformanceCounter time of that position, in 100 ns (0 = none read yet).
    pub device_qpc_100ns: i64,
    /// Pts of the frame at that position; [`PTS_NONE`] = unknown (nothing timed has reached the device).
    pub device_pts_100ns: i64,
    /// Audio pts minus video position at the last measured sync reference; positive = audio ahead.
    pub sync_error_100ns: i64,
    /// Sync references measured.
    pub syncs: u32,
    /// Sync references that arrived while the output was not playing or before any timed frame reached the
    /// device.
    pub syncs_unmeasured: u32,
    /// Corrections that dropped frames (audio behind) or padded silence (audio ahead).
    pub drop_corrections: u32,
    pub pad_corrections: u32,
    pub dropped_frames: u64,
    pub padded_frames: u64,
    /// `PATH_*`, as in the open info (was reserved, zero, before 2026-11-24).
    pub path: u32,
    pub reserved: [u32; 5],
}

/// The `KSDATAFORMAT_SUBTYPE_IEC61937_*` GUID of an `IEC_*` value (the probe's GUIDs).
pub fn iec_subtype_guid(subtype: u32) -> Option<Guid> {
    match subtype {
        IEC_AC3 => Some(SUBTYPE_IEC61937_DOLBY_DIGITAL),
        IEC_EAC3 => Some(SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS),
        IEC_DTS => Some(SUBTYPE_IEC61937_DTS),
        IEC_DTS_HD => Some(SUBTYPE_IEC61937_DTS_HD),
        IEC_MLP => Some(SUBTYPE_IEC61937_DOLBY_MLP),
        _ => None,
    }
}

/// ksmedia.h's mask for a speaker count without a choice to make: mono, stereo, 7.1 surround.
pub fn default_mask(speakers: u32) -> Option<u32> {
    match speakers {
        1 => Some(SPEAKER_MONO),
        2 => Some(SPEAKER_STEREO),
        8 => Some(SPEAKER_7POINT1_SURROUND),
        _ => None,
    }
}

fn resolve_mask(mask: u32, speakers: u32) -> Result<u32, i32> {
    let mask = if mask == 0 {
        default_mask(speakers).ok_or(E_INVALIDARG)?
    } else {
        mask
    };
    if mask & !SPEAKER_BITS != 0 || mask.count_ones() != speakers {
        return Err(E_INVALIDARG);
    }
    Ok(mask)
}

/// A checked format: the caller's [`BaleAudioFormat`] with its defaults filled in, and the
/// `WAVEFORMATEXTENSIBLE` (or `_IEC61937`) block `IsFormatSupported` and `Initialize` read.
#[derive(Clone, Copy)]
pub struct Format {
    pub abi: BaleAudioFormat,
    pub wave: WaveFormat,
}

impl Format {
    /// Checks `requested` against the rules in `include/bale_audio_out.h`; `E_INVALIDARG` otherwise.
    pub fn from_abi(requested: &BaleAudioFormat) -> Result<Self, i32> {
        let f = *requested;
        if f.reserved != [0; 3] {
            return Err(E_INVALIDARG);
        }
        let mut abi = f;
        let wave = match f.kind {
            KIND_PCM => {
                let valid = if f.valid_bits == 0 {
                    f.container_bits
                } else {
                    f.valid_bits
                };
                if f.iec_subtype != IEC_NONE
                    || f.encoded_rate != 0
                    || f.encoded_channels != 0
                    || !(1..=8).contains(&f.channels)
                    || !(8_000..=384_000).contains(&f.sample_rate)
                    || !matches!(f.container_bits, 16 | 24 | 32)
                    || !(8..=f.container_bits).contains(&valid)
                {
                    return Err(E_INVALIDARG);
                }
                abi.valid_bits = valid;
                abi.channel_mask = resolve_mask(f.channel_mask, f.channels)?;
                WaveFormat::extensible(
                    f.channels as u16,
                    f.sample_rate,
                    f.container_bits as u16,
                    valid as u16,
                    abi.channel_mask,
                    SUBTYPE_PCM,
                )
            }
            KIND_IEC61937 => {
                let subtype = iec_subtype_guid(f.iec_subtype).ok_or(E_INVALIDARG)?;
                // IEC 61937 always travels as 16-bit IEC 60958 words.
                if !matches!(f.channels, 2 | 8)
                    || !IEC_RATES.contains(&f.sample_rate)
                    || !matches!(f.container_bits, 0 | 16)
                    || !matches!(f.valid_bits, 0 | 16)
                {
                    return Err(E_INVALIDARG);
                }
                abi.container_bits = 16;
                abi.valid_bits = 16;
                if f.encoded_rate == 0 {
                    if f.encoded_channels != 0 {
                        return Err(E_INVALIDARG);
                    }
                    abi.channel_mask = resolve_mask(f.channel_mask, f.channels)?;
                    WaveFormat::link(f.channels as u16, f.sample_rate, abi.channel_mask, subtype)
                } else {
                    let encoded_channels = if f.encoded_channels == 0 {
                        f.channels
                    } else {
                        f.encoded_channels
                    };
                    // 768000: the "encoded rate" some stacks put there for high bit rate links (probe rows).
                    if !(1..=8).contains(&encoded_channels)
                        || !(8_000..=768_000).contains(&f.encoded_rate)
                    {
                        return Err(E_INVALIDARG);
                    }
                    abi.encoded_channels = encoded_channels;
                    abi.channel_mask = resolve_mask(f.channel_mask, encoded_channels)?;
                    WaveFormat::iec61937(
                        f.channels as u16,
                        f.sample_rate,
                        abi.channel_mask,
                        subtype,
                        f.encoded_rate,
                        encoded_channels,
                    )
                }
            }
            _ => return Err(E_INVALIDARG),
        };
        Ok(Self { abi, wave })
    }

    /// Bytes per frame (`nBlockAlign`): what every write must be a multiple of.
    pub fn block_align(&self) -> usize {
        usize::from({ self.wave.block_align })
    }

    pub fn rate(&self) -> u32 {
        self.abi.sample_rate
    }

    pub fn is_bitstream(&self) -> bool {
        self.abi.kind == KIND_IEC61937
    }
}

/// Whether `ring_ms` is 0 (default) or within [`MIN_RING_MS`]..=[`MAX_RING_MS`].
pub fn valid_ring_ms(ring_ms: u32) -> bool {
    ring_ms == 0 || (MIN_RING_MS..=MAX_RING_MS).contains(&ring_ms)
}

/// Ring capacity in frames for `ring_ms` (0 = [`DEFAULT_RING_MS`]), at least two device buffers so the
/// writer can stay a period ahead.
pub fn ring_frames(ring_ms: u32, rate: u32, buffer_frames: u32) -> u32 {
    let ms = if ring_ms == 0 {
        DEFAULT_RING_MS
    } else {
        ring_ms
    };
    let frames = (u64::from(ms) * u64::from(rate)).div_ceil(1000);
    (frames.min(u64::from(u32::MAX)) as u32).max(buffer_frames.saturating_mul(2))
}

/// Whether the plain exclusive `Initialize`'s answer sends create on to the offload paths.
pub fn tries_offload(hr: i32) -> bool {
    matches!(
        hr,
        AUDCLNT_E_UNSUPPORTED_FORMAT | AUDCLNT_E_OFFLOAD_MODE_ONLY
    )
}

/// The buffer duration and periodicity of an offload open (event-driven): the default period exclusive (periodicity
/// = buffer), two default periods shared (periodicity 0), each fitted into `GetBufferSizeLimits` when it answered
/// with usable limits.
pub fn offload_timing(shared: bool, default_period: i64, limits: Option<(i64, i64)>) -> (i64, i64) {
    let base = if shared {
        default_period.saturating_mul(2)
    } else {
        default_period
    };
    let buffer = match limits {
        Some((minimum, maximum)) if minimum > 0 && minimum <= maximum => {
            base.clamp(minimum, maximum)
        }
        _ => base,
    };
    (buffer, if shared { 0 } else { buffer })
}

/// The buffer duration for `frames` after `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`, exactly as Microsoft's
/// `IAudioClient::Initialize` remarks compute it: `(REFERENCE_TIME)((10000.0 * 1000 / nSamplesPerSec *
/// nFrames) + 0.5)`.
pub fn aligned_period_hns(frames: u32, rate: u32) -> i64 {
    (10_000.0 * 1_000.0 / f64::from(rate) * f64::from(frames) + 0.5) as i64
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_audio_out_abi_version() -> u32 {
    ABI_VERSION
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audio_probe::{
        EXTENSIBLE_CB_SIZE, IEC61937_CB_SIZE, SPEAKER_5POINT1, SPEAKER_7POINT1,
        WAVE_FORMAT_EXTENSIBLE, WAVEFORMATEX_SIZE,
    };
    use std::mem::{offset_of, size_of};

    fn bytes(format: &WaveFormat) -> Vec<u8> {
        // SAFETY: plain old data, viewed as its own bytes.
        unsafe {
            std::slice::from_raw_parts(
                std::ptr::from_ref(format).cast::<u8>(),
                size_of::<WaveFormat>(),
            )
        }
        .to_vec()
    }

    fn pcm(channels: u32, rate: u32, bits: u32, valid: u32, mask: u32) -> BaleAudioFormat {
        BaleAudioFormat {
            channels,
            sample_rate: rate,
            container_bits: bits,
            valid_bits: valid,
            channel_mask: mask,
            ..BaleAudioFormat::default()
        }
    }

    fn iec(channels: u32, rate: u32, subtype: u32, mask: u32) -> BaleAudioFormat {
        BaleAudioFormat {
            kind: KIND_IEC61937,
            channels,
            sample_rate: rate,
            iec_subtype: subtype,
            channel_mask: mask,
            ..BaleAudioFormat::default()
        }
    }

    /// The layouts `include/bale_audio_out.h` declares (tests/audio_out_header.rs compiles it against these).
    #[test]
    fn abi_structs_have_the_header_layout() {
        assert_eq!(size_of::<BaleAudioFormat>(), 48);
        assert_eq!(offset_of!(BaleAudioFormat, encoded_channels), 32);
        assert_eq!(size_of::<BaleAudioOutOpenInfo>(), 80);
        assert_eq!(offset_of!(BaleAudioOutOpenInfo, default_period_100ns), 32);
        assert_eq!(offset_of!(BaleAudioOutOpenInfo, buffer_frames), 56);
        assert_eq!(offset_of!(BaleAudioOutOpenInfo, realigned), 68);
        assert_eq!(offset_of!(BaleAudioOutOpenInfo, path), 72);
        assert_eq!(offset_of!(BaleAudioOutOpenInfo, offload_hr), 76);
        assert_eq!(size_of::<BaleAudioOutStats>(), 152);
        assert_eq!(offset_of!(BaleAudioOutStats, frames_written), 32);
        assert_eq!(offset_of!(BaleAudioOutStats, device_position_100ns), 64);
        assert_eq!(offset_of!(BaleAudioOutStats, sync_error_100ns), 88);
        assert_eq!(offset_of!(BaleAudioOutStats, syncs), 96);
        assert_eq!(offset_of!(BaleAudioOutStats, dropped_frames), 112);
        assert_eq!(offset_of!(BaleAudioOutStats, path), 128);
        assert_eq!(offset_of!(BaleAudioOutStats, reserved), 132);
        assert_eq!(bale_audio_out_abi_version(), 1);
    }

    /// The console's accepted format becomes the probe's "PCM 7.1 24-bit in 32" row, byte for byte.
    #[test]
    fn console_format_is_the_probe_row_it_accepted() {
        let format = Format::from_abi(&CONSOLE_PCM_7_1).unwrap();
        let row = crate::audio_probe::candidates()
            .into_iter()
            .find(|c| c.name == "PCM 7.1 24-bit in 32")
            .unwrap();
        assert_eq!(bytes(&format.wave), bytes(&row.format));
        let w = format.wave;
        let (tag, channels, rate, average, block, bits, cb_size, valid, mask, sub) = (
            w.format_tag,
            w.channels,
            w.samples_per_sec,
            w.avg_bytes_per_sec,
            w.block_align,
            w.bits_per_sample,
            w.cb_size,
            w.valid_bits_per_sample,
            w.channel_mask,
            w.sub_format,
        );
        assert_eq!(
            (tag, channels, rate, average, block, bits),
            (WAVE_FORMAT_EXTENSIBLE, 8, 48_000, 1_536_000, 32, 32)
        );
        assert_eq!(
            (cb_size, valid, mask, sub),
            (EXTENSIBLE_CB_SIZE, 24, 0x63F, SUBTYPE_PCM)
        );
        assert_eq!(format.block_align(), 32);
        assert_eq!(format.abi, CONSOLE_PCM_7_1);
        assert!(!format.is_bitstream());
        // Only 18 + cbSize bytes are declared; the IEC 61937 tail stays zero.
        assert!(
            bytes(&w)[WAVEFORMATEX_SIZE + usize::from(EXTENSIBLE_CB_SIZE)..]
                .iter()
                .all(|&b| b == 0)
        );
    }

    #[test]
    fn pcm_defaults_fill_in_and_bad_formats_are_refused() {
        let stereo = Format::from_abi(&pcm(2, 48_000, 16, 0, 0)).unwrap();
        assert_eq!(
            (stereo.abi.valid_bits, stereo.abi.channel_mask),
            (16, SPEAKER_STEREO)
        );
        assert_eq!(stereo.block_align(), 4);
        let mono = Format::from_abi(&pcm(1, 44_100, 24, 0, 0)).unwrap();
        assert_eq!(
            (mono.abi.channel_mask, mono.block_align(), {
                mono.wave.avg_bytes_per_sec
            }),
            (SPEAKER_MONO, 3, 132_300)
        );
        // 5.1 has two ksmedia masks (back 0x3F, side 0x60F): the caller must say which.
        assert_eq!(
            Format::from_abi(&pcm(6, 48_000, 16, 0, 0)).err(),
            Some(E_INVALIDARG)
        );
        let side = Format::from_abi(&pcm(6, 48_000, 16, 0, 0x60F)).unwrap();
        assert_eq!({ side.wave.channel_mask }, 0x60F);
        for bad in [
            pcm(0, 48_000, 16, 0, 0),
            pcm(9, 48_000, 16, 0, 0),
            pcm(2, 7_999, 16, 0, 0),
            pcm(2, 384_001, 16, 0, 0),
            pcm(2, 48_000, 8, 0, 0),
            pcm(2, 48_000, 20, 0, 0),
            pcm(2, 48_000, 24, 32, 0),
            pcm(2, 48_000, 32, 7, 0),
            // A mask must name exactly the channels, with ksmedia's speaker bits only.
            pcm(2, 48_000, 16, 0, 0x7),
            pcm(8, 48_000, 32, 24, 0x3F),
            pcm(1, 48_000, 16, 0, 0x8000_0000),
            BaleAudioFormat {
                iec_subtype: IEC_AC3,
                ..pcm(2, 48_000, 16, 0, 0)
            },
            BaleAudioFormat {
                encoded_rate: 48_000,
                ..pcm(2, 48_000, 16, 0, 0)
            },
            BaleAudioFormat {
                reserved: [0, 1, 0],
                ..pcm(2, 48_000, 16, 0, 0)
            },
            BaleAudioFormat {
                kind: 2,
                ..pcm(2, 48_000, 16, 0, 0)
            },
        ] {
            assert_eq!(Format::from_abi(&bad).err(), Some(E_INVALIDARG), "{bad:?}");
        }
    }

    /// IEC 61937 links as the probe asks them: a plain `WAVEFORMATEXTENSIBLE` (cbSize 22) without an encoded
    /// rate, `WAVEFORMATEXTENSIBLE_IEC61937` (cbSize 34) with one, byte for byte the same rows.
    #[test]
    fn iec61937_formats_match_the_probe_rows() {
        let all = crate::audio_probe::candidates();
        let row = |name: &str| bytes(&all.iter().find(|c| c.name == name).unwrap().format);
        let ac3 = Format::from_abi(&iec(2, 48_000, IEC_AC3, 0)).unwrap();
        assert_eq!(bytes(&ac3.wave), row("AC-3"));
        assert_eq!(
            (
                ac3.abi.container_bits,
                ac3.abi.valid_bits,
                ac3.abi.channel_mask
            ),
            (16, 16, SPEAKER_STEREO)
        );
        assert!(ac3.is_bitstream());
        let eac3 = Format::from_abi(&iec(2, 192_000, IEC_EAC3, 0)).unwrap();
        assert_eq!(bytes(&eac3.wave), row("E-AC-3"));
        assert_eq!(
            bytes(&Format::from_abi(&iec(2, 48_000, IEC_DTS, 0)).unwrap().wave),
            row("DTS")
        );
        assert_eq!(
            bytes(
                &Format::from_abi(&iec(8, 192_000, IEC_DTS_HD, 0))
                    .unwrap()
                    .wave
            ),
            row("DTS-HD")
        );
        assert_eq!(
            bytes(&Format::from_abi(&iec(8, 192_000, IEC_MLP, 0)).unwrap().wave),
            row("TrueHD")
        );
        // Microsoft's E-AC-3 example: 48 kHz 5.1 content on a 192 kHz two-line link, the back 5.1 mask.
        let example = Format::from_abi(&BaleAudioFormat {
            encoded_rate: 48_000,
            encoded_channels: 6,
            ..iec(2, 192_000, IEC_EAC3, SPEAKER_5POINT1)
        })
        .unwrap();
        assert_eq!(bytes(&example.wave), row("E-AC-3 [iec 48k 5.1]"));
        let w = example.wave;
        assert_eq!(
            (
                { w.cb_size },
                { w.encoded_samples_per_sec },
                { w.encoded_channel_count },
                { w.encoded_average_bytes_per_sec },
                { w.sub_format },
            ),
            (
                IEC61937_CB_SIZE,
                48_000,
                6,
                0,
                SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS
            )
        );
        assert_eq!(
            (example.abi.encoded_channels, example.abi.channel_mask),
            (6, SPEAKER_5POINT1)
        );
        // Microsoft's TrueHD (MAT 1.0) example: 96 kHz content, the wide 7.1 mask.
        let truehd = Format::from_abi(&BaleAudioFormat {
            encoded_rate: 96_000,
            ..iec(8, 192_000, IEC_MLP, SPEAKER_7POINT1)
        })
        .unwrap();
        assert_eq!(bytes(&truehd.wave), row("TrueHD [iec 96k wide 7.1]"));
        assert_eq!(truehd.abi.encoded_channels, 8);
        let hbr = Format::from_abi(&BaleAudioFormat {
            encoded_rate: 768_000,
            ..iec(8, 192_000, IEC_DTS_HD, 0)
        })
        .unwrap();
        assert_eq!(bytes(&hbr.wave), row("DTS-HD [iec 768k]"));
        for bad in [
            iec(2, 48_000, IEC_NONE, 0),
            iec(2, 48_000, 6, 0),
            iec(6, 48_000, IEC_AC3, 0),
            iec(2, 96_000, IEC_AC3, 0),
            BaleAudioFormat {
                container_bits: 32,
                ..iec(2, 48_000, IEC_AC3, 0)
            },
            BaleAudioFormat {
                valid_bits: 24,
                ..iec(2, 48_000, IEC_AC3, 0)
            },
            // Decoded channels without a decoded rate, a 5.1 mask without 6 decoded channels.
            BaleAudioFormat {
                encoded_channels: 6,
                ..iec(2, 192_000, IEC_EAC3, 0)
            },
            BaleAudioFormat {
                encoded_rate: 48_000,
                ..iec(2, 192_000, IEC_EAC3, SPEAKER_5POINT1)
            },
            BaleAudioFormat {
                encoded_rate: 768_001,
                ..iec(8, 192_000, IEC_DTS_HD, 0)
            },
        ] {
            assert_eq!(Format::from_abi(&bad).err(), Some(E_INVALIDARG), "{bad:?}");
        }
        assert_eq!(iec_subtype_guid(IEC_NONE), None);
    }

    #[test]
    fn ring_and_period_sizes() {
        assert!(valid_ring_ms(0) && valid_ring_ms(50) && valid_ring_ms(1000));
        assert!(!valid_ring_ms(49) && !valid_ring_ms(1001));
        // 500 ms at 48 kHz; 200 ms at 44.1 kHz rounds up; never under two device buffers.
        assert_eq!(ring_frames(0, 48_000, 480), 24_000);
        assert_eq!(ring_frames(200, 44_100, 441), 8_820);
        assert_eq!(ring_frames(1, 44_100, 0), 45);
        assert_eq!(ring_frames(50, 48_000, 2_048), 4_096);
        // Microsoft's re-align formula: 10 ms at 48 kHz, an aligned 448-frame buffer at 44.1 kHz.
        assert_eq!(aligned_period_hns(480, 48_000), 100_000);
        assert_eq!(aligned_period_hns(448, 44_100), 101_587);
        assert_eq!(aligned_period_hns(512, 48_000), 106_667);
        assert_eq!(aligned_period_hns(1, 192_000), 52);
    }

    #[test]
    fn offload_comes_after_unsupported_and_fits_its_limits() {
        assert!(tries_offload(AUDCLNT_E_UNSUPPORTED_FORMAT));
        assert!(tries_offload(AUDCLNT_E_OFFLOAD_MODE_ONLY));
        assert!(!tries_offload(AUDCLNT_E_DEVICE_IN_USE));
        assert!(!tries_offload(AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED));
        // Exclusive: the default period, periodicity = buffer; shared: two periods, periodicity 0.
        assert_eq!(offload_timing(false, 100_000, None), (100_000, 100_000));
        assert_eq!(offload_timing(true, 100_000, None), (200_000, 0));
        // Fitted into the limits, unless they make no sense.
        assert_eq!(
            offload_timing(false, 100_000, Some((200_000, 400_000))),
            (200_000, 200_000)
        );
        assert_eq!(
            offload_timing(true, 100_000, Some((10_000, 150_000))),
            (150_000, 0)
        );
        assert_eq!(
            offload_timing(false, 100_000, Some((0, 400_000))),
            (100_000, 100_000)
        );
        assert_eq!(
            offload_timing(true, 100_000, Some((500_000, 400_000))),
            (200_000, 0)
        );
    }

    #[test]
    fn lost_devices_are_told_apart_from_failures() {
        assert!(is_device_lost(AUDCLNT_E_DEVICE_INVALIDATED));
        assert!(is_device_lost(AUDCLNT_E_RESOURCES_INVALIDATED));
        assert!(!is_device_lost(AUDCLNT_E_DEVICE_IN_USE));
        assert!(!is_device_lost(E_TIMEOUT));
    }
}
