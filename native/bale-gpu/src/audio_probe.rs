//! Debug audio passthrough capability probe (docs/audio-passthrough.md, "Capability probe"): which encoded
//! formats the console's default render endpoint would accept as an IEC 61937 bitstream right now.
//!
//! `bale_gpu_audio_probe` (Windows) gets an `IAudioClient` the UWP way, `MediaDevice::GetDefaultAudioRenderId`
//! then `ActivateAudioInterfaceAsync` (a render endpoint with `IAudioClient` is one of the activations
//! Microsoft lists as safe off the UI thread), reports the shared-mode mix format and asks
//! `IsFormatSupported` in exclusive mode for each of [`candidates`]. It never calls `Initialize` or `Start`:
//! the client is only asked and then released, so the probe cannot take the endpoint away from Media
//! Foundation, even while a film plays.
//!
//! `mmdevapi.dll` is never imported, like `d3d12.dll`: the caller passes the loaded module and
//! `ActivateAudioInterfaceAsync` is found with `GetProcAddress`, so a console without it cannot stop
//! `bale_gpu.dll` from loading. Formats, GUIDs, names and the report text are platform independent and
//! unit tested here; only the COM calls are Windows-only.
//!
//! Format layouts follow Microsoft's "Representing Formats for IEC 61937 Transmissions"
//! (`WAVEFORMATEXTENSIBLE_IEC61937`) and the Windows SDK's ksmedia.h / mmreg.h.

use std::fmt::{self, Write as _};

/// mmreg.h format tags.
pub const WAVE_FORMAT_PCM: u16 = 0x0001;
pub const WAVE_FORMAT_IEEE_FLOAT: u16 = 0x0003;
pub const WAVE_FORMAT_DTS: u16 = 0x0008;
pub const WAVE_FORMAT_DOLBY_AC3_SPDIF: u16 = 0x0092;
pub const WAVE_FORMAT_EXTENSIBLE: u16 = 0xFFFE;

/// `cbSize` of a `WAVEFORMATEXTENSIBLE` and of a `WAVEFORMATEXTENSIBLE_IEC61937`.
pub const EXTENSIBLE_CB_SIZE: u16 = 22;
pub const IEC61937_CB_SIZE: u16 = 34;
/// `sizeof(WAVEFORMATEX)`: the bytes before `cbSize`'s extra data.
pub const WAVEFORMATEX_SIZE: usize = 18;

/// ksmedia.h speaker masks. Two-channel IEC 60958 links use stereo; eight-channel high-bit-rate links use
/// 7.1 surround. Microsoft's IEC 61937 examples use the obsolete back 5.1 and wide 7.1 masks instead.
pub const SPEAKER_STEREO: u32 = 0x3; // KSAUDIO_SPEAKER_STEREO
pub const SPEAKER_5POINT1: u32 = 0x3F; // KSAUDIO_SPEAKER_5POINT1 (back; "obsolete" in ksmedia.h)
pub const SPEAKER_7POINT1: u32 = 0xFF; // KSAUDIO_SPEAKER_7POINT1 (wide; "obsolete" in ksmedia.h)
pub const SPEAKER_7POINT1_SURROUND: u32 = 0x63F; // KSAUDIO_SPEAKER_7POINT1_SURROUND

/// A GUID in Windows' memory layout (`Data1`-`Data3` little-endian on x64, `Data4` bytes in order), so a
/// [`WaveFormat`] is byte-for-byte what `IsFormatSupported` reads.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Guid {
    pub data1: u32,
    pub data2: u16,
    pub data3: u16,
    pub data4: [u8; 8],
}

impl Guid {
    /// Parses `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`, the form of ksmedia.h's `DEFINE_GUIDSTRUCT`, so the
    /// constants below are the header's strings verbatim. A malformed string fails the build.
    pub const fn parse(text: &str) -> Self {
        let bytes = text.as_bytes();
        assert!(bytes.len() == 36, "a GUID string has 36 characters");
        let mut value = 0u128;
        let mut index = 0;
        while index < bytes.len() {
            let byte = bytes[index];
            if matches!(index, 8 | 13 | 18 | 23) {
                assert!(byte == b'-', "a GUID string has dashes at 8, 13, 18 and 23");
            } else {
                let digit = match byte {
                    b'0'..=b'9' => byte - b'0',
                    b'a'..=b'f' => byte - b'a' + 10,
                    b'A'..=b'F' => byte - b'A' + 10,
                    _ => panic!("a GUID string has only hex digits and dashes"),
                };
                value = (value << 4) | digit as u128;
            }
            index += 1;
        }
        Self {
            data1: (value >> 96) as u32,
            data2: (value >> 80) as u16,
            data3: (value >> 64) as u16,
            data4: (value as u64).to_be_bytes(),
        }
    }
}

impl fmt::Display for Guid {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let d = self.data4;
        write!(
            f,
            "{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
            self.data1, self.data2, self.data3, d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7]
        )
    }
}

/// ksmedia.h `KSDATAFORMAT_SUBTYPE_*` (Windows SDK 10.0.26100).
pub const SUBTYPE_PCM: Guid = Guid::parse("00000001-0000-0010-8000-00aa00389b71");
pub const SUBTYPE_IEEE_FLOAT: Guid = Guid::parse("00000003-0000-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_DIGITAL: Guid =
    Guid::parse("00000092-0000-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS: Guid =
    Guid::parse("0000000a-0cea-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS_ATMOS: Guid =
    Guid::parse("0000010a-0cea-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DTS: Guid = Guid::parse("00000008-0000-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DTS_HD: Guid = Guid::parse("0000000b-0cea-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_MLP: Guid = Guid::parse("0000000c-0cea-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_MAT20: Guid = Guid::parse("0000010c-0cea-0010-8000-00aa00389b71");
pub const SUBTYPE_IEC61937_DOLBY_MAT21: Guid = Guid::parse("0000030c-0cea-0010-8000-00aa00389b71");

const SUBTYPE_NAMES: [(Guid, &str); 10] = [
    (SUBTYPE_PCM, "PCM"),
    (SUBTYPE_IEEE_FLOAT, "IEEE_FLOAT"),
    (SUBTYPE_IEC61937_DOLBY_DIGITAL, "IEC61937_DOLBY_DIGITAL"),
    (
        SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS,
        "IEC61937_DOLBY_DIGITAL_PLUS",
    ),
    (
        SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS_ATMOS,
        "IEC61937_DOLBY_DIGITAL_PLUS_ATMOS",
    ),
    (SUBTYPE_IEC61937_DTS, "IEC61937_DTS"),
    (SUBTYPE_IEC61937_DTS_HD, "IEC61937_DTS_HD"),
    (SUBTYPE_IEC61937_DOLBY_MLP, "IEC61937_DOLBY_MLP"),
    (SUBTYPE_IEC61937_DOLBY_MAT20, "IEC61937_DOLBY_MAT20"),
    (SUBTYPE_IEC61937_DOLBY_MAT21, "IEC61937_DOLBY_MAT21"),
];

/// The `KSDATAFORMAT_SUBTYPE_` name without its prefix, else the GUID.
pub fn subtype_name(subtype: Guid) -> String {
    SUBTYPE_NAMES
        .iter()
        .find(|(guid, _)| *guid == subtype)
        .map_or_else(|| subtype.to_string(), |(_, name)| (*name).to_owned())
}

/// The mmreg.h `WAVE_FORMAT_` name, else the hex tag.
pub fn tag_name(tag: u16) -> String {
    match tag {
        WAVE_FORMAT_PCM => "WAVE_FORMAT_PCM".to_owned(),
        WAVE_FORMAT_IEEE_FLOAT => "WAVE_FORMAT_IEEE_FLOAT".to_owned(),
        WAVE_FORMAT_DTS => "WAVE_FORMAT_DTS".to_owned(),
        WAVE_FORMAT_DOLBY_AC3_SPDIF => "WAVE_FORMAT_DOLBY_AC3_SPDIF".to_owned(),
        WAVE_FORMAT_EXTENSIBLE => "WAVE_FORMAT_EXTENSIBLE".to_owned(),
        _ => format!("tag 0x{tag:04X}"),
    }
}

/// ksmedia.h's `WAVEFORMATEXTENSIBLE_IEC61937`, packed like the SDK's: `WAVEFORMATEX` (18 bytes), the
/// `WAVEFORMATEXTENSIBLE` tail (22) and the IEC 61937 tail (12), 52 bytes. `cb_size` says how much of it a
/// format uses: 0 (a plain `WAVEFORMATEX` with a format tag), 22 (`WAVEFORMATEXTENSIBLE`) or 34 (all of
/// it); the unused tail stays zero. Read packed fields by value only.
#[repr(C, packed(1))]
#[derive(Clone, Copy, Default)]
pub struct WaveFormat {
    pub format_tag: u16,
    pub channels: u16,
    pub samples_per_sec: u32,
    pub avg_bytes_per_sec: u32,
    pub block_align: u16,
    pub bits_per_sample: u16,
    pub cb_size: u16,
    pub valid_bits_per_sample: u16,
    pub channel_mask: u32,
    pub sub_format: Guid,
    /// The decoded content's sample rate (IEC 61937 tail).
    pub encoded_samples_per_sec: u32,
    /// The decoded content's channel count (IEC 61937 tail).
    pub encoded_channel_count: u32,
    /// The content's byte rate; 0, "ignored for this format" in Microsoft's examples (IEC 61937 tail).
    pub encoded_average_bytes_per_sec: u32,
}

impl WaveFormat {
    /// A `WAVEFORMATEX` with a legacy format tag (`cbSize` 0): `channels` 16-bit words at `rate`, as
    /// `WAVE_FORMAT_DOLBY_AC3_SPDIF` is documented (2 ch, 48 kHz, block 4, 192000 bytes/s).
    pub fn tagged(tag: u16, channels: u16, rate: u32) -> Self {
        let block_align = channels * 2;
        Self {
            format_tag: tag,
            channels,
            samples_per_sec: rate,
            avg_bytes_per_sec: rate * u32::from(block_align),
            block_align,
            bits_per_sample: 16,
            ..Self::default()
        }
    }

    /// A `WAVEFORMATEXTENSIBLE` (`cbSize` 22): `channels` samples of `bits` (`valid_bits` used) at `rate`.
    pub fn extensible(
        channels: u16,
        rate: u32,
        bits: u16,
        valid_bits: u16,
        mask: u32,
        sub_format: Guid,
    ) -> Self {
        let block_align = channels * (bits / 8);
        Self {
            format_tag: WAVE_FORMAT_EXTENSIBLE,
            channels,
            samples_per_sec: rate,
            avg_bytes_per_sec: rate * u32::from(block_align),
            block_align,
            bits_per_sample: bits,
            cb_size: EXTENSIBLE_CB_SIZE,
            valid_bits_per_sample: valid_bits,
            channel_mask: mask,
            sub_format,
            ..Self::default()
        }
    }

    /// An IEC 61937 link (always 16-bit words) as a `WAVEFORMATEXTENSIBLE` (`cbSize` 22).
    pub fn link(channels: u16, rate: u32, mask: u32, sub_format: Guid) -> Self {
        Self::extensible(channels, rate, 16, 16, mask, sub_format)
    }

    /// An IEC 61937 link as a `WAVEFORMATEXTENSIBLE_IEC61937` (`cbSize` 34), with the decoded content's
    /// rate and channel count; `dwAverageBytesPerSec` 0 as in Microsoft's examples.
    pub fn iec61937(
        channels: u16,
        rate: u32,
        mask: u32,
        sub_format: Guid,
        encoded_rate: u32,
        encoded_channels: u32,
    ) -> Self {
        Self {
            cb_size: IEC61937_CB_SIZE,
            encoded_samples_per_sec: encoded_rate,
            encoded_channel_count: encoded_channels,
            ..Self::link(channels, rate, mask, sub_format)
        }
    }

    /// Reads a format block of `bytes` (a `WAVEFORMATEX` and its `cbSize` extra bytes): `None` if shorter
    /// than a `WAVEFORMATEX`. Only the bytes `cbSize` covers are taken (at most 52); the rest stays zero.
    pub fn read(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < WAVEFORMATEX_SIZE {
            return None;
        }
        let cb_size = u16::from_le_bytes([bytes[16], bytes[17]]);
        let length = (WAVEFORMATEX_SIZE + usize::from(cb_size))
            .min(bytes.len())
            .min(size_of::<Self>());
        let mut format = Self::default();
        // SAFETY: `WaveFormat` is plain old data with alignment 1, and `length` is within both buffers.
        unsafe {
            std::ptr::copy_nonoverlapping(
                bytes.as_ptr(),
                std::ptr::from_mut(&mut format).cast::<u8>(),
                length,
            );
        }
        Some(format)
    }

    /// Whether `cbSize` covers the `WAVEFORMATEXTENSIBLE` tail under the extensible tag.
    pub fn is_extensible(&self) -> bool {
        let (tag, cb_size) = (self.format_tag, self.cb_size);
        tag == WAVE_FORMAT_EXTENSIBLE && cb_size >= EXTENSIBLE_CB_SIZE
    }

    /// One line: layout (`tag` legacy `WAVEFORMATEX`, `ext` `WAVEFORMATEXTENSIBLE`, `iec`
    /// `WAVEFORMATEXTENSIBLE_IEC61937`), link channels, rate, bits, mask, subtype and decoded content.
    pub fn describe(&self) -> String {
        let (tag, channels, rate, bits, cb_size) = (
            self.format_tag,
            self.channels,
            self.samples_per_sec,
            self.bits_per_sample,
            self.cb_size,
        );
        if !self.is_extensible() {
            return format!("{} {channels} ch {rate} Hz {bits}-bit", tag_name(tag));
        }
        let iec = cb_size >= IEC61937_CB_SIZE;
        let mut text = format!(
            "{} {channels} ch {rate} Hz {bits}-bit",
            if iec { "iec" } else { "ext" }
        );
        let (valid, mask, sub_format) = (
            self.valid_bits_per_sample,
            self.channel_mask,
            self.sub_format,
        );
        if valid != bits {
            let _ = write!(text, " ({valid} valid)");
        }
        let _ = write!(text, " mask 0x{mask:X} {}", subtype_name(sub_format));
        if iec {
            let (encoded_rate, encoded_channels) =
                (self.encoded_samples_per_sec, self.encoded_channel_count);
            let _ = write!(text, ", decoded {encoded_rate} Hz {encoded_channels} ch");
        }
        text
    }
}

/// One format the probe asks about.
#[derive(Clone, Copy)]
pub struct Candidate {
    /// Codec, plus the layout in brackets where a codec is asked about more than once. No commas.
    pub name: &'static str,
    pub format: WaveFormat,
}

impl Candidate {
    /// Encoded audio for passthrough, as opposed to the PCM context rows.
    pub fn is_bitstream(&self) -> bool {
        !(self.format.is_extensible() && { self.format.sub_format } == SUBTYPE_PCM)
    }
}

/// What the probe asks, in report order: PCM for context, then each codec as an IEC 61937 bitstream.
///
/// AC-3 and DTS ride one IEC 60958 line (2 ch) at the content rate, E-AC-3 at four times it (192 kHz);
/// DTS-HD and TrueHD (MAT) need high bit rate: eight lines at 192 kHz, a 768 kHz IEC 60958 frame rate. Each
/// codec is asked first as a plain `WAVEFORMATEXTENSIBLE` with the link's speaker mask (`ext`), then, where
/// stacks differ, as a `WAVEFORMATEXTENSIBLE_IEC61937` (`iec`) that also describes the decoded content:
/// 48 kHz content, Microsoft's documented examples verbatim (E-AC-3 48 kHz 5.1, TrueHD 96 kHz wide 7.1),
/// and the 768 kHz "encoded rate" some stacks put there. AC-3 and DTS are also asked with legacy tags.
pub fn candidates() -> Vec<Candidate> {
    let pcm = |channels, bits, valid, mask| {
        WaveFormat::extensible(channels, 48_000, bits, valid, mask, SUBTYPE_PCM)
    };
    let line = |rate, sub_format| WaveFormat::link(2, rate, SPEAKER_STEREO, sub_format);
    // Microsoft's E-AC-3 example: 48 kHz 5.1 content on a 192 kHz line.
    let line_iec =
        |sub_format| WaveFormat::iec61937(2, 192_000, SPEAKER_5POINT1, sub_format, 48_000, 6);
    let hbr = |sub_format| WaveFormat::link(8, 192_000, SPEAKER_7POINT1_SURROUND, sub_format);
    let hbr_iec = |sub_format, rate| {
        WaveFormat::iec61937(8, 192_000, SPEAKER_7POINT1_SURROUND, sub_format, rate, 8)
    };
    // Microsoft's TrueHD (MAT 1.0) example: 96 kHz content, the wide 7.1 mask.
    let truehd_example = WaveFormat::iec61937(
        8,
        192_000,
        SPEAKER_7POINT1,
        SUBTYPE_IEC61937_DOLBY_MLP,
        96_000,
        8,
    );
    // One row per question; the golden list in the tests shows each as the report prints it.
    #[rustfmt::skip]
    let rows = [
        ("PCM 2.0 16-bit", pcm(2, 16, 16, SPEAKER_STEREO)),
        ("PCM 7.1 16-bit", pcm(8, 16, 16, SPEAKER_7POINT1_SURROUND)),
        ("PCM 7.1 24-bit in 32", pcm(8, 32, 24, SPEAKER_7POINT1_SURROUND)),
        ("PCM 7.1 24-bit packed", pcm(8, 24, 24, SPEAKER_7POINT1_SURROUND)),
        ("AC-3", line(48_000, SUBTYPE_IEC61937_DOLBY_DIGITAL)),
        ("AC-3 [tag]", WaveFormat::tagged(WAVE_FORMAT_DOLBY_AC3_SPDIF, 2, 48_000)),
        ("E-AC-3", line(192_000, SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS)),
        ("E-AC-3 [iec 48k 5.1]", line_iec(SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS)),
        ("E-AC-3 Atmos [iec 48k 5.1]", line_iec(SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS_ATMOS)),
        ("DTS", line(48_000, SUBTYPE_IEC61937_DTS)),
        ("DTS [tag]", WaveFormat::tagged(WAVE_FORMAT_DTS, 2, 48_000)),
        ("DTS-HD", hbr(SUBTYPE_IEC61937_DTS_HD)),
        ("DTS-HD [iec 48k 7.1]", hbr_iec(SUBTYPE_IEC61937_DTS_HD, 48_000)),
        ("DTS-HD [iec 768k]", hbr_iec(SUBTYPE_IEC61937_DTS_HD, 768_000)),
        ("TrueHD", hbr(SUBTYPE_IEC61937_DOLBY_MLP)),
        ("TrueHD [iec 48k 7.1]", hbr_iec(SUBTYPE_IEC61937_DOLBY_MLP, 48_000)),
        ("TrueHD [iec 768k]", hbr_iec(SUBTYPE_IEC61937_DOLBY_MLP, 768_000)),
        ("TrueHD [iec 96k wide 7.1]", truehd_example),
        ("MAT 2.0 [iec 48k 7.1]", hbr_iec(SUBTYPE_IEC61937_DOLBY_MAT20, 48_000)),
        ("MAT 2.1 [iec 48k 7.1]", hbr_iec(SUBTYPE_IEC61937_DOLBY_MAT21, 48_000)),
    ];
    rows.into_iter()
        .map(|(name, format)| Candidate { name, format })
        .collect()
}

pub const S_OK: i32 = 0;
pub const S_FALSE: i32 = 1;
pub const AUDCLNT_E_UNSUPPORTED_FORMAT: i32 = 0x8889_0008u32 as i32;
/// `HRESULT_FROM_WIN32(ERROR_TIMEOUT)`: the probe's own code for an activation that never completed.
pub const E_TIMEOUT: i32 = 0x8007_05B4u32 as i32;
/// `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` (`E_NOT_SET`): the probe's own code for no default render endpoint.
pub const E_NOT_SET: i32 = 0x8007_0490u32 as i32;
/// `HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)`: `mmdevapi.dll` without `ActivateAudioInterfaceAsync`.
pub const E_PROC_NOT_FOUND: i32 = 0x8007_007Fu32 as i32;

/// winerror.h and audioclient.h (`AUDCLNT_ERR(n)` = 0x88890000 + n) names the probe can meet.
const HRESULT_NAMES: [(u32, &str); 61] = [
    (0x0000_0000, "S_OK"),
    (0x0000_0001, "S_FALSE"),
    (0x8000_000A, "E_PENDING"),
    (0x8000_000E, "E_ILLEGAL_METHOD_CALL"),
    (0x8000_4001, "E_NOTIMPL"),
    (0x8000_4002, "E_NOINTERFACE"),
    (0x8000_4003, "E_POINTER"),
    (0x8000_4004, "E_ABORT"),
    (0x8000_4005, "E_FAIL"),
    (0x8000_FFFF, "E_UNEXPECTED"),
    (0x8001_0106, "RPC_E_CHANGED_MODE"),
    (0x8004_0154, "REGDB_E_CLASSNOTREG"),
    (0x8004_01F0, "CO_E_NOTINITIALIZED"),
    (0x8007_0002, "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)"),
    (0x8007_0005, "E_ACCESSDENIED"),
    (0x8007_000E, "E_OUTOFMEMORY"),
    (0x8007_0032, "HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)"),
    (0x8007_0057, "E_INVALIDARG"),
    (0x8007_007F, "HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)"),
    (
        0x8007_048F,
        "HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED)",
    ),
    (0x8007_0490, "E_NOT_SET"),
    (0x8007_05B4, "HRESULT_FROM_WIN32(ERROR_TIMEOUT)"),
    (0x8889_0001, "AUDCLNT_E_NOT_INITIALIZED"),
    (0x8889_0002, "AUDCLNT_E_ALREADY_INITIALIZED"),
    (0x8889_0003, "AUDCLNT_E_WRONG_ENDPOINT_TYPE"),
    (0x8889_0004, "AUDCLNT_E_DEVICE_INVALIDATED"),
    (0x8889_0005, "AUDCLNT_E_NOT_STOPPED"),
    (0x8889_0006, "AUDCLNT_E_BUFFER_TOO_LARGE"),
    (0x8889_0007, "AUDCLNT_E_OUT_OF_ORDER"),
    (0x8889_0008, "AUDCLNT_E_UNSUPPORTED_FORMAT"),
    (0x8889_0009, "AUDCLNT_E_INVALID_SIZE"),
    (0x8889_000A, "AUDCLNT_E_DEVICE_IN_USE"),
    (0x8889_000B, "AUDCLNT_E_BUFFER_OPERATION_PENDING"),
    (0x8889_000C, "AUDCLNT_E_THREAD_NOT_REGISTERED"),
    (0x8889_000E, "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"),
    (0x8889_000F, "AUDCLNT_E_ENDPOINT_CREATE_FAILED"),
    (0x8889_0010, "AUDCLNT_E_SERVICE_NOT_RUNNING"),
    (0x8889_0011, "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED"),
    (0x8889_0012, "AUDCLNT_E_EXCLUSIVE_MODE_ONLY"),
    (0x8889_0013, "AUDCLNT_E_BUFDURATION_PERIOD_NOT_EQUAL"),
    (0x8889_0014, "AUDCLNT_E_EVENTHANDLE_NOT_SET"),
    (0x8889_0015, "AUDCLNT_E_INCORRECT_BUFFER_SIZE"),
    (0x8889_0016, "AUDCLNT_E_BUFFER_SIZE_ERROR"),
    (0x8889_0017, "AUDCLNT_E_CPUUSAGE_EXCEEDED"),
    (0x8889_0018, "AUDCLNT_E_BUFFER_ERROR"),
    (0x8889_0019, "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED"),
    (0x8889_0020, "AUDCLNT_E_INVALID_DEVICE_PERIOD"),
    (0x8889_0021, "AUDCLNT_E_INVALID_STREAM_FLAG"),
    (0x8889_0022, "AUDCLNT_E_ENDPOINT_OFFLOAD_NOT_CAPABLE"),
    (0x8889_0023, "AUDCLNT_E_OUT_OF_OFFLOAD_RESOURCES"),
    (0x8889_0024, "AUDCLNT_E_OFFLOAD_MODE_ONLY"),
    (0x8889_0025, "AUDCLNT_E_NONOFFLOAD_MODE_ONLY"),
    (0x8889_0026, "AUDCLNT_E_RESOURCES_INVALIDATED"),
    (0x8889_0027, "AUDCLNT_E_RAW_MODE_UNSUPPORTED"),
    (0x8889_0028, "AUDCLNT_E_ENGINE_PERIODICITY_LOCKED"),
    (0x8889_0029, "AUDCLNT_E_ENGINE_FORMAT_LOCKED"),
    (0x8889_0030, "AUDCLNT_E_HEADTRACKING_ENABLED"),
    (0x8889_0040, "AUDCLNT_E_HEADTRACKING_UNSUPPORTED"),
    (0x8889_0041, "AUDCLNT_E_EFFECT_NOT_AVAILABLE"),
    (0x8889_0042, "AUDCLNT_E_EFFECT_STATE_READ_ONLY"),
    (0x8889_0043, "AUDCLNT_E_POST_VOLUME_LOOPBACK_UNSUPPORTED"),
];

/// The header name of `hr`, if the probe knows it.
pub fn hresult_name(hr: i32) -> Option<&'static str> {
    HRESULT_NAMES
        .iter()
        .find(|(code, _)| *code == hr as u32)
        .map(|(_, name)| *name)
}

/// `S_OK`, `NAME (0xXXXXXXXX)` or the bare hex for codes without a known name.
pub fn hresult_text(hr: i32) -> String {
    match (hr, hresult_name(hr)) {
        (S_OK, _) => "S_OK".to_owned(),
        (_, Some(name)) => format!("{name} (0x{:08X})", hr as u32),
        (_, None) => format!("0x{:08X}", hr as u32),
    }
}

/// What `IsFormatSupported` said about one format.
pub fn verdict(hr: i32) -> String {
    match hr {
        S_OK => "S_OK (accepted)".to_owned(),
        S_FALSE => "S_FALSE (closest match only)".to_owned(),
        AUDCLNT_E_UNSUPPORTED_FORMAT => "AUDCLNT_E_UNSUPPORTED_FORMAT (rejected)".to_owned(),
        _ => hresult_text(hr),
    }
}

/// One report row: `name: format -> verdict`.
pub fn result_line(candidate: &Candidate, hr: i32) -> String {
    format!(
        "{}: {} -> {}",
        candidate.name,
        candidate.format.describe(),
        verdict(hr)
    )
}

/// The report's last line (also logged by the app): accepted bitstreams and PCM by name, the rejected
/// count, and any other answer with its count.
pub fn summary(results: &[(Candidate, i32)]) -> String {
    let accepted = |bitstream: bool| {
        let names: Vec<&str> = results
            .iter()
            .filter(|(candidate, hr)| *hr == S_OK && candidate.is_bitstream() == bitstream)
            .map(|(candidate, _)| candidate.name)
            .collect();
        if names.is_empty() {
            "none".to_owned()
        } else {
            names.join(", ")
        }
    };
    let rejected = results
        .iter()
        .filter(|(_, hr)| *hr == AUDCLNT_E_UNSUPPORTED_FORMAT)
        .count();
    let mut other: Vec<(i32, usize)> = Vec::new();
    for &(_, hr) in results {
        if hr == S_OK || hr == AUDCLNT_E_UNSUPPORTED_FORMAT {
            continue;
        }
        match other.iter_mut().find(|(code, _)| *code == hr) {
            Some((_, count)) => *count += 1,
            None => other.push((hr, 1)),
        }
    }
    let mut text = format!(
        "Summary: bitstream accepted: {}; PCM accepted: {}; {rejected} of {} rejected",
        accepted(true),
        accepted(false),
        results.len()
    );
    for (index, (hr, count)) in other.iter().enumerate() {
        let separator = if index == 0 { "; other: " } else { ", " };
        let _ = write!(text, "{separator}{} x{count}", hresult_text(*hr));
    }
    text.push('\n');
    text
}

/// The summary when the probe stopped before asking about any format.
pub fn not_probed(step: &str, hr: i32) -> String {
    format!("Summary: not probed ({} at {step})\n", hresult_text(hr))
}

#[cfg(all(windows, feature = "debug-probes"))]
pub use wasapi::bale_gpu_audio_probe;

/// The probe's COM side. Activation (`Mta`, `activation_function`, `activate_client`) is shared with the
/// exclusive-mode output (`audio_out`), which gets its `IAudioClient` the same way; the probe itself
/// (`bale_gpu_audio_probe`) exists only with the `debug-probes` feature.
#[cfg(windows)]
pub(crate) mod wasapi {
    use std::ffi::c_void;
    use std::sync::mpsc::{RecvTimeoutError, SyncSender, sync_channel};
    use std::sync::{Mutex, PoisonError};
    use std::time::{Duration, Instant};

    use windows::Win32::Foundation::{
        E_INVALIDARG, E_NOINTERFACE, E_POINTER, E_UNEXPECTED, HMODULE,
    };
    use windows::Win32::Media::Audio::{
        IActivateAudioInterfaceAsyncOperation, IActivateAudioInterfaceCompletionHandler,
        IActivateAudioInterfaceCompletionHandler_Impl, IAudioClient,
    };
    use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
    use windows::Win32::System::LibraryLoader::GetProcAddress;
    use windows::core::{GUID, HRESULT, IUnknown, Interface, PCWSTR, Ref, implement, s};

    use super::{E_PROC_NOT_FOUND, E_TIMEOUT};

    // The debug probe's own needs.
    #[cfg(feature = "debug-probes")]
    use super::{
        Candidate, E_NOT_SET, WAVEFORMATEX_SIZE, WaveFormat, candidates, hresult_text, not_probed,
        result_line, summary,
    };
    #[cfg(feature = "debug-probes")]
    use std::fmt::Write as _;
    #[cfg(feature = "debug-probes")]
    use std::panic::{AssertUnwindSafe, catch_unwind};
    #[cfg(feature = "debug-probes")]
    use windows::Media::Devices::{AudioDeviceRole, MediaDevice};
    #[cfg(feature = "debug-probes")]
    use windows::Win32::Media::Audio::{
        AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMOPTIONS_NONE, AudioCategory_Movie,
        AudioClientProperties, IAudioClient2, WAVEFORMATEX,
    };
    #[cfg(feature = "debug-probes")]
    use windows::Win32::System::Com::CoTaskMemFree;
    #[cfg(feature = "debug-probes")]
    use windows::core::BOOL;

    /// How long the probe waits for `ActivateCompleted`. Activation of a render `IAudioClient` normally
    /// completes in milliseconds; the wait only bounds a wedged audio service.
    const ACTIVATION_TIMEOUT: Duration = Duration::from_secs(2);

    /// mmdeviceapi.h `ActivateAudioInterfaceAsync`, called through `GetProcAddress` (never imported).
    pub(crate) type ActivateAudioInterfaceAsync = unsafe extern "system" fn(
        PCWSTR,
        *const GUID,
        *const c_void,
        *mut c_void,
        *mut *mut c_void,
    ) -> HRESULT;

    /// Debug audio passthrough probe (DEBUG_SERVER `GET /audio-probe`; docs/audio-passthrough.md,
    /// "Capability probe"): the default render endpoint's mix format and the exclusive-mode
    /// `IsFormatSupported` answer for each IEC 61937 (and PCM) candidate. Never initializes or starts the
    /// client. Writes the plain-text report as UTF-8 into `report` and returns the bytes written; a longer
    /// report is cut at a character boundary. Blocks the calling thread, which must be an MTA thread and
    /// never the UI thread, for the activation (at most 2 s) and the format questions.
    ///
    /// `mmdevapi` is the caller's loaded `mmdevapi.dll`, or null (reported, nothing asked).
    ///
    /// # Safety
    /// `mmdevapi` must be null or a module the caller keeps loaded for the call; `report` must point to
    /// `report_capacity` writable bytes.
    #[cfg(feature = "debug-probes")]
    #[unsafe(no_mangle)]
    pub unsafe extern "C" fn bale_gpu_audio_probe(
        mmdevapi: HMODULE,
        report: *mut u8,
        report_capacity: u32,
    ) -> u32 {
        if report.is_null() {
            return 0;
        }
        let text = catch_unwind(AssertUnwindSafe(|| run(mmdevapi))).unwrap_or_else(|_| {
            "Audio passthrough probe: panicked\nSummary: not probed (panic)\n".to_owned()
        });
        // SAFETY: the caller guarantees `report_capacity` writable bytes.
        unsafe { crate::write_report(&text, report, report_capacity) }
    }

    /// A failed step: its name and HRESULT, nothing else (no system error text). Only the debug reports (the
    /// probe and the Initialize variants) name the step; the audio output keeps the HRESULT.
    pub(crate) struct Failed(
        #[cfg_attr(not(feature = "debug-probes"), allow(dead_code))] pub(crate) &'static str,
        pub(crate) i32,
    );

    trait StepResult<T> {
        fn step(self, name: &'static str) -> Result<T, Failed>;
    }

    impl<T> StepResult<T> for windows::core::Result<T> {
        fn step(self, name: &'static str) -> Result<T, Failed> {
            self.map_err(|error| Failed(name, error.code().0))
        }
    }

    /// The calling thread in the multithreaded apartment for the probe, balanced on drop. A
    /// single-threaded apartment is refused: the probe blocks while it waits for the activation callback.
    pub(crate) struct Mta;

    impl Mta {
        pub(crate) fn join() -> Result<Self, Failed> {
            // SAFETY: plain COM initialization of this thread; `Drop` balances it only on success (S_OK
            // or S_FALSE, already in the MTA).
            let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
            if hr.is_err() {
                return Err(Failed("CoInitializeEx (call from an MTA thread)", hr.0));
            }
            Ok(Self)
        }
    }

    impl Drop for Mta {
        fn drop(&mut self) {
            // SAFETY: balances the successful CoInitializeEx in `join`, after every COM object is released.
            unsafe { CoUninitialize() };
        }
    }

    /// Signals the waiting probe; it reads the result itself with `GetActivateResult`. `#[implement]`
    /// objects are agile (IAgileObject and the free-threaded marshaler), which ActivateAudioInterfaceAsync
    /// requires of its completion handler.
    #[implement(IActivateAudioInterfaceCompletionHandler)]
    struct Completion(Mutex<Option<SyncSender<()>>>);

    impl IActivateAudioInterfaceCompletionHandler_Impl for Completion_Impl {
        fn ActivateCompleted(
            &self,
            _operation: Ref<IActivateAudioInterfaceAsyncOperation>,
        ) -> windows::core::Result<()> {
            if let Some(sender) = self.0.lock().unwrap_or_else(PoisonError::into_inner).take() {
                // A full or closed channel only means the probe already stopped waiting.
                let _ = sender.try_send(());
            }
            Ok(())
        }
    }

    #[cfg(feature = "debug-probes")]
    fn run(mmdevapi: HMODULE) -> String {
        let mut report = String::from(
            "Audio passthrough probe: default render endpoint, exclusive-mode IsFormatSupported only (never Initialize or Start)\n",
        );
        let mta = match Mta::join() {
            Ok(mta) => mta,
            Err(Failed(step, hr)) => {
                let _ = writeln!(report, "Error: {} at {step}", hresult_text(hr));
                report.push_str(&not_probed(step, hr));
                return report;
            }
        };
        // Every COM object lives inside `probe`, so all are released before the apartment is left.
        match probe(mmdevapi, &mut report) {
            Ok(results) => report.push_str(&summary(&results)),
            Err(Failed(step, hr)) => {
                let _ = writeln!(report, "Error: {} at {step}", hresult_text(hr));
                report.push_str(&not_probed(step, hr));
            }
        }
        drop(mta);
        report
    }

    #[cfg(feature = "debug-probes")]
    fn probe(mmdevapi: HMODULE, report: &mut String) -> Result<Vec<(Candidate, i32)>, Failed> {
        let activate = activation_function(mmdevapi)?;
        let id = MediaDevice::GetDefaultAudioRenderId(AudioDeviceRole::Default)
            .step("MediaDevice.GetDefaultAudioRenderId")?;
        if id.is_empty() {
            return Err(Failed(
                "MediaDevice.GetDefaultAudioRenderId (no default render endpoint)",
                E_NOT_SET,
            ));
        }
        let (client, elapsed) = activate_client(activate, PCWSTR(id.as_ptr()))?;
        let _ = writeln!(
            report,
            "Activation: IAudioClient in {} ms (MediaDevice default render id, ActivateAudioInterfaceAsync off the UI thread)",
            elapsed.as_millis()
        );

        match client.cast::<IAudioClient2>() {
            Ok(client2) => {
                let properties = AudioClientProperties {
                    cbSize: size_of::<AudioClientProperties>() as u32,
                    bIsOffload: BOOL(0),
                    eCategory: AudioCategory_Movie,
                    Options: AUDCLNT_STREAMOPTIONS_NONE,
                };
                // SAFETY: a live client; `properties` is a valid local for the call.
                let hr = unsafe { client2.SetClientProperties(&properties) }
                    .map_or_else(|error| error.code().0, |()| 0);
                let _ = writeln!(
                    report,
                    "Client properties: IAudioClient2 SetClientProperties(AudioCategory_Movie, offload off) -> {}",
                    hresult_text(hr)
                );
            }
            Err(error) => {
                let _ = writeln!(
                    report,
                    "Client properties: no IAudioClient2 ({}); default category",
                    hresult_text(error.code().0)
                );
            }
        }

        // SAFETY: a live client; the returned block is read and freed with CoTaskMemFree below.
        match unsafe { client.GetMixFormat() } {
            Ok(mix) if !mix.is_null() => {
                // SAFETY: GetMixFormat returns a WAVEFORMATEX and its cbSize extra bytes, read before the free.
                let format = unsafe { read_format(mix) };
                // SAFETY: allocated by the audio engine with CoTaskMemAlloc; not used afterwards.
                unsafe { CoTaskMemFree(Some(mix.cast_const().cast())) };
                let text = format.map_or_else(|| "unreadable".to_owned(), |f| f.describe());
                let _ = writeln!(report, "Mix format (shared mode): {text}");
            }
            Ok(_) => {
                let _ = writeln!(report, "Mix format (shared mode): none");
            }
            Err(error) => {
                let _ = writeln!(
                    report,
                    "Mix format (shared mode): {}",
                    hresult_text(error.code().0)
                );
            }
        }

        let _ = writeln!(
            report,
            "Exclusive mode, IsFormatSupported (S_OK = accepted, AUDCLNT_E_UNSUPPORTED_FORMAT = rejected):"
        );
        let mut results = Vec::new();
        for candidate in candidates() {
            let format = std::ptr::from_ref(&candidate.format).cast::<WAVEFORMATEX>();
            // SAFETY: a live client; `format` points to a whole `WaveFormat`, which covers the WAVEFORMATEX and
            // the cbSize extra bytes it declares. Exclusive mode takes no closest-match out-pointer.
            let hr = unsafe { client.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, format, None) };
            let _ = writeln!(report, "  {}", result_line(&candidate, hr.0));
            results.push((candidate, hr.0));
        }
        // Only asked, never initialized: released here, before the report returns.
        drop(client);
        Ok(results)
    }

    pub(crate) fn activation_function(
        module: HMODULE,
    ) -> Result<ActivateAudioInterfaceAsync, Failed> {
        if module.is_invalid() {
            return Err(Failed(
                "mmdevapi.dll (not loaded by the caller)",
                E_INVALIDARG.0,
            ));
        }
        // SAFETY: the caller keeps the module loaded for the call; the name is a static C string.
        let function =
            unsafe { GetProcAddress(module, s!("ActivateAudioInterfaceAsync")) }.ok_or(Failed(
                "GetProcAddress(ActivateAudioInterfaceAsync)",
                E_PROC_NOT_FOUND,
            ))?;
        // SAFETY: mmdeviceapi.h's documented signature; both are plain `system` function pointers.
        Ok(unsafe {
            std::mem::transmute::<unsafe extern "system" fn() -> isize, ActivateAudioInterfaceAsync>(
                function,
            )
        })
    }

    /// Activates `IAudioClient` on `device` and waits for the callback, at most [`ACTIVATION_TIMEOUT`];
    /// returns the client and how long the activation took. The calling thread must be in the MTA.
    pub(crate) fn activate_client(
        activate: ActivateAudioInterfaceAsync,
        device: PCWSTR,
    ) -> Result<(IAudioClient, Duration), Failed> {
        let (sender, receiver) = sync_channel(1);
        let handler: IActivateAudioInterfaceCompletionHandler =
            Completion(Mutex::new(Some(sender))).into();
        let started = Instant::now();
        let mut raw = std::ptr::null_mut();
        // SAFETY: `device` is a live null-terminated string, the IID is static, no activation parameters,
        // and the handler and out-slot are valid for the call. Windows holds its own handler reference
        // until the operation completes.
        let hr = unsafe {
            activate(
                device,
                &IAudioClient::IID,
                std::ptr::null(),
                handler.as_raw(),
                &mut raw,
            )
        };
        if hr.is_err() {
            return Err(Failed("ActivateAudioInterfaceAsync", hr.0));
        }
        if raw.is_null() {
            return Err(Failed(
                "ActivateAudioInterfaceAsync (no operation)",
                E_POINTER.0,
            ));
        }
        // SAFETY: a successful call returned an owned operation reference.
        let operation = unsafe { IActivateAudioInterfaceAsyncOperation::from_raw(raw) };
        match receiver.recv_timeout(ACTIVATION_TIMEOUT) {
            Ok(()) => {}
            Err(RecvTimeoutError::Timeout) => {
                return Err(Failed(
                    "ActivateCompleted (no callback within 2000 ms)",
                    E_TIMEOUT,
                ));
            }
            Err(RecvTimeoutError::Disconnected) => {
                return Err(Failed(
                    "ActivateCompleted (handler released without a callback)",
                    E_UNEXPECTED.0,
                ));
            }
        }
        let elapsed = started.elapsed();
        let mut activated = HRESULT(0);
        let mut unknown: Option<IUnknown> = None;
        // SAFETY: the completion callback ran; both out-slots are valid locals.
        unsafe { operation.GetActivateResult(&mut activated, &mut unknown) }
            .step("GetActivateResult")?;
        if activated.is_err() {
            return Err(Failed("ActivateAudioInterfaceAsync result", activated.0));
        }
        let client = unknown
            .ok_or(Failed("GetActivateResult (no interface)", E_NOINTERFACE.0))?
            .cast::<IAudioClient>()
            .step("IAudioClient")?;
        Ok((client, elapsed))
    }

    /// Reads a `WAVEFORMATEX` block of `18 + cbSize` bytes.
    ///
    /// # Safety
    /// `format` must point to a `WAVEFORMATEX` followed by its `cbSize` extra bytes.
    #[cfg(feature = "debug-probes")]
    unsafe fn read_format(format: *const WAVEFORMATEX) -> Option<WaveFormat> {
        // SAFETY: the caller guarantees a readable WAVEFORMATEX (packed, alignment 1).
        let header = unsafe { format.read_unaligned() };
        let length = WAVEFORMATEX_SIZE + usize::from(header.cbSize);
        // SAFETY: the caller guarantees `length` readable bytes.
        WaveFormat::read(unsafe { std::slice::from_raw_parts(format.cast::<u8>(), length) })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bytes(format: &WaveFormat) -> &[u8] {
        // SAFETY: plain old data, viewed as its own bytes.
        unsafe {
            std::slice::from_raw_parts(
                std::ptr::from_ref(format).cast::<u8>(),
                size_of::<WaveFormat>(),
            )
        }
    }

    /// ksmedia.h's strings, transcribed a second time, and the GUID memory layout.
    #[test]
    fn subtypes_match_ksmedia_h() {
        for (name, text) in [
            ("PCM", "00000001-0000-0010-8000-00aa00389b71"),
            ("IEEE_FLOAT", "00000003-0000-0010-8000-00aa00389b71"),
            (
                "IEC61937_DOLBY_DIGITAL",
                "00000092-0000-0010-8000-00aa00389b71",
            ),
            (
                "IEC61937_DOLBY_DIGITAL_PLUS",
                "0000000a-0cea-0010-8000-00aa00389b71",
            ),
            (
                "IEC61937_DOLBY_DIGITAL_PLUS_ATMOS",
                "0000010a-0cea-0010-8000-00aa00389b71",
            ),
            ("IEC61937_DTS", "00000008-0000-0010-8000-00aa00389b71"),
            ("IEC61937_DTS_HD", "0000000b-0cea-0010-8000-00aa00389b71"),
            ("IEC61937_DOLBY_MLP", "0000000c-0cea-0010-8000-00aa00389b71"),
            (
                "IEC61937_DOLBY_MAT20",
                "0000010c-0cea-0010-8000-00aa00389b71",
            ),
            (
                "IEC61937_DOLBY_MAT21",
                "0000030c-0cea-0010-8000-00aa00389b71",
            ),
        ] {
            assert_eq!(subtype_name(Guid::parse(text)), name);
            assert_eq!(Guid::parse(text).to_string(), text);
        }
        let dts_hd = SUBTYPE_IEC61937_DTS_HD;
        assert_eq!(
            (dts_hd.data1, dts_hd.data2, dts_hd.data3),
            (0xB, 0x0CEA, 0x10)
        );
        assert_eq!(dts_hd.data4, [0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71]);
        let upper = Guid::parse("E6327CAD-DCEC-4949-AE8A-991E976A79D2");
        assert_eq!(subtype_name(upper), "e6327cad-dcec-4949-ae8a-991e976a79d2");
    }

    #[test]
    fn layouts_match_the_sdk_structs() {
        // WAVEFORMATEX 18, WAVEFORMATEXTENSIBLE 40, WAVEFORMATEXTENSIBLE_IEC61937 52 bytes, all packed.
        assert_eq!((size_of::<Guid>(), align_of::<WaveFormat>()), (16, 1));
        assert_eq!(std::mem::offset_of!(WaveFormat, cb_size), 16);
        assert_eq!(
            std::mem::offset_of!(WaveFormat, valid_bits_per_sample),
            WAVEFORMATEX_SIZE
        );
        assert_eq!(std::mem::offset_of!(WaveFormat, channel_mask), 20);
        assert_eq!(std::mem::offset_of!(WaveFormat, sub_format), 24);
        assert_eq!(
            std::mem::offset_of!(WaveFormat, encoded_samples_per_sec),
            40
        );
        assert_eq!(WAVEFORMATEX_SIZE + usize::from(EXTENSIBLE_CB_SIZE), 40);
        assert_eq!(
            WAVEFORMATEX_SIZE + usize::from(IEC61937_CB_SIZE),
            size_of::<WaveFormat>()
        );
        assert_eq!(size_of::<WaveFormat>(), 52);
    }

    #[cfg(windows)]
    #[test]
    fn layouts_match_the_windows_crate() {
        use windows::Win32::Media::Audio::{WAVEFORMATEX, WAVEFORMATEXTENSIBLE};
        assert_eq!(size_of::<WAVEFORMATEX>(), WAVEFORMATEX_SIZE);
        assert_eq!(size_of::<WAVEFORMATEXTENSIBLE>(), 40);
        assert_eq!(size_of::<windows::core::GUID>(), size_of::<Guid>());
    }

    /// Everything the probe asks, as the report prints it: one golden line per candidate.
    #[test]
    fn candidates_are_the_documented_list() {
        let expected = "\
PCM 2.0 16-bit: ext 2 ch 48000 Hz 16-bit mask 0x3 PCM
PCM 7.1 16-bit: ext 8 ch 48000 Hz 16-bit mask 0x63F PCM
PCM 7.1 24-bit in 32: ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
PCM 7.1 24-bit packed: ext 8 ch 48000 Hz 24-bit mask 0x63F PCM
AC-3: ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL
AC-3 [tag]: WAVE_FORMAT_DOLBY_AC3_SPDIF 2 ch 48000 Hz 16-bit
E-AC-3: ext 2 ch 192000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL_PLUS
E-AC-3 [iec 48k 5.1]: iec 2 ch 192000 Hz 16-bit mask 0x3F IEC61937_DOLBY_DIGITAL_PLUS, decoded 48000 Hz 6 ch
E-AC-3 Atmos [iec 48k 5.1]: iec 2 ch 192000 Hz 16-bit mask 0x3F IEC61937_DOLBY_DIGITAL_PLUS_ATMOS, decoded 48000 Hz 6 ch
DTS: ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DTS
DTS [tag]: WAVE_FORMAT_DTS 2 ch 48000 Hz 16-bit
DTS-HD: ext 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DTS_HD
DTS-HD [iec 48k 7.1]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DTS_HD, decoded 48000 Hz 8 ch
DTS-HD [iec 768k]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DTS_HD, decoded 768000 Hz 8 ch
TrueHD: ext 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DOLBY_MLP
TrueHD [iec 48k 7.1]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DOLBY_MLP, decoded 48000 Hz 8 ch
TrueHD [iec 768k]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DOLBY_MLP, decoded 768000 Hz 8 ch
TrueHD [iec 96k wide 7.1]: iec 8 ch 192000 Hz 16-bit mask 0xFF IEC61937_DOLBY_MLP, decoded 96000 Hz 8 ch
MAT 2.0 [iec 48k 7.1]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DOLBY_MAT20, decoded 48000 Hz 8 ch
MAT 2.1 [iec 48k 7.1]: iec 8 ch 192000 Hz 16-bit mask 0x63F IEC61937_DOLBY_MAT21, decoded 48000 Hz 8 ch
";
        let actual: String = candidates()
            .iter()
            .map(|c| format!("{}: {}\n", c.name, c.format.describe()))
            .collect();
        assert_eq!(actual, expected);
    }

    #[test]
    fn every_candidate_is_self_consistent() {
        let all = candidates();
        let mut names = std::collections::HashSet::new();
        for candidate in &all {
            let name = candidate.name;
            assert!(names.insert(name), "{name} twice");
            assert!(
                !name.contains(','),
                "{name}: the summary joins names with commas"
            );
            let f = candidate.format;
            let (channels, bits, block, rate) = (
                f.channels,
                f.bits_per_sample,
                f.block_align,
                f.samples_per_sec,
            );
            let (average, valid, mask, cb_size) = (
                f.avg_bytes_per_sec,
                f.valid_bits_per_sample,
                f.channel_mask,
                f.cb_size,
            );
            assert_eq!(
                u32::from(block),
                u32::from(channels) * u32::from(bits) / 8,
                "{name}"
            );
            assert_eq!(average, rate * u32::from(block), "{name}");
            // The unused tail stays zero: IsFormatSupported reads only 18 + cbSize bytes anyway.
            let used = WAVEFORMATEX_SIZE + usize::from(cb_size);
            assert!(bytes(&f)[used..].iter().all(|&byte| byte == 0), "{name}");
            if f.is_extensible() {
                // A plain WAVEFORMATEXTENSIBLE's mask names the link's channels; an IEC 61937 one's names the
                // decoded content, as in Microsoft's examples (5.1 on the two-channel E-AC-3 link).
                let speakers = match cb_size {
                    IEC61937_CB_SIZE => f.encoded_channel_count,
                    _ => u32::from(channels),
                };
                assert_eq!(mask.count_ones(), speakers, "{name}");
                assert!(valid <= bits, "{name}");
            }
            if candidate.is_bitstream() {
                // IEC 61937 always travels as 16-bit IEC 60958 words, on two or eight lines.
                assert_eq!(bits, 16, "{name}");
                assert!(matches!(channels, 2 | 8), "{name}");
            }
        }
        assert_eq!(all.iter().filter(|c| c.is_bitstream()).count(), 16);
    }

    /// Microsoft's "Representing Formats for IEC 61937 Transmissions" examples, every field.
    #[test]
    fn microsoft_examples_are_reproduced_verbatim() {
        let all = candidates();
        let find = |name: &str| all.iter().find(|c| c.name == name).unwrap().format;
        let eac3 = WaveFormat {
            format_tag: WAVE_FORMAT_EXTENSIBLE,
            channels: 2,
            samples_per_sec: 192_000,
            avg_bytes_per_sec: 768_000,
            block_align: 4,
            bits_per_sample: 16,
            cb_size: 34,
            valid_bits_per_sample: 16,
            channel_mask: SPEAKER_5POINT1,
            sub_format: SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS,
            encoded_samples_per_sec: 48_000,
            encoded_channel_count: 6,
            encoded_average_bytes_per_sec: 0,
        };
        assert_eq!(bytes(&find("E-AC-3 [iec 48k 5.1]")), bytes(&eac3));
        let truehd = WaveFormat {
            format_tag: WAVE_FORMAT_EXTENSIBLE,
            channels: 8,
            samples_per_sec: 192_000,
            avg_bytes_per_sec: 3_072_000,
            block_align: 16,
            bits_per_sample: 16,
            cb_size: 34,
            valid_bits_per_sample: 16,
            channel_mask: SPEAKER_7POINT1,
            sub_format: SUBTYPE_IEC61937_DOLBY_MLP,
            encoded_samples_per_sec: 96_000,
            encoded_channel_count: 8,
            encoded_average_bytes_per_sec: 0,
        };
        assert_eq!(bytes(&find("TrueHD [iec 96k wide 7.1]")), bytes(&truehd));
        // WAVE_FORMAT_DOLBY_AC3_SPDIF: 2 ch, 48 kHz, 192000 bytes/s, block 4, 16-bit, no extra bytes.
        let ac3 = WaveFormat {
            format_tag: WAVE_FORMAT_DOLBY_AC3_SPDIF,
            channels: 2,
            samples_per_sec: 48_000,
            avg_bytes_per_sec: 192_000,
            block_align: 4,
            bits_per_sample: 16,
            ..WaveFormat::default()
        };
        assert_eq!(bytes(&find("AC-3 [tag]")), bytes(&ac3));
    }

    #[test]
    fn formats_read_back_from_their_bytes() {
        // A typical shared-mode mix: WAVEFORMATEXTENSIBLE float stereo.
        let float = WaveFormat::extensible(2, 48_000, 32, 32, SPEAKER_STEREO, SUBTYPE_IEEE_FLOAT);
        let read = WaveFormat::read(&bytes(&float)[..40]).unwrap();
        assert_eq!(
            read.describe(),
            "ext 2 ch 48000 Hz 32-bit mask 0x3 IEEE_FLOAT"
        );
        // cbSize bounds the read: bytes past a 22-byte tail are never picked up.
        let mut longer = bytes(&float).to_vec();
        longer[40..].fill(0xEE);
        assert_eq!(
            { WaveFormat::read(&longer).unwrap().encoded_samples_per_sec },
            0
        );
        // A plain WAVEFORMATEX: float, 2 ch, 48000 Hz, 384000 bytes/s, block 8, 32-bit, cbSize 0.
        let plain = [
            3, 0, 2, 0, 0x80, 0xBB, 0, 0, 0, 0xDC, 5, 0, 8, 0, 32, 0, 0, 0,
        ];
        let read = WaveFormat::read(&plain).unwrap();
        assert_eq!(
            read.describe(),
            "WAVE_FORMAT_IEEE_FLOAT 2 ch 48000 Hz 32-bit"
        );
        assert!(WaveFormat::read(&plain[..17]).is_none());
        let odd = Guid::parse("12345678-9abc-def0-1234-56789abcdef0");
        let unknown = WaveFormat::extensible(6, 48_000, 16, 16, SPEAKER_5POINT1, odd);
        assert_eq!(
            unknown.describe(),
            "ext 6 ch 48000 Hz 16-bit mask 0x3F 12345678-9abc-def0-1234-56789abcdef0"
        );
    }

    #[test]
    fn rows_and_hresults_use_the_header_names() {
        let all = candidates();
        let row = |name: &str, hr| result_line(all.iter().find(|c| c.name == name).unwrap(), hr);
        assert_eq!(
            row("AC-3", AUDCLNT_E_UNSUPPORTED_FORMAT),
            "AC-3: ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL -> AUDCLNT_E_UNSUPPORTED_FORMAT (rejected)"
        );
        assert_eq!(
            row("AC-3 [tag]", S_OK),
            "AC-3 [tag]: WAVE_FORMAT_DOLBY_AC3_SPDIF 2 ch 48000 Hz 16-bit -> S_OK (accepted)"
        );
        assert!(row("DTS", 0x8007_0005u32 as i32).ends_with(" -> E_ACCESSDENIED (0x80070005)"));
        assert_eq!(verdict(S_FALSE), "S_FALSE (closest match only)");
        assert_eq!(hresult_text(S_OK), "S_OK");
        assert_eq!(
            hresult_text(0x8889_000Eu32 as i32),
            "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED (0x8889000E)"
        );
        assert_eq!(
            hresult_text(0x8000_000Eu32 as i32),
            "E_ILLEGAL_METHOD_CALL (0x8000000E)"
        );
        assert_eq!(
            hresult_text(E_TIMEOUT),
            "HRESULT_FROM_WIN32(ERROR_TIMEOUT) (0x800705B4)"
        );
        assert_eq!(hresult_text(0x8889_00FFu32 as i32), "0x888900FF");
        let mut codes: Vec<u32> = HRESULT_NAMES.iter().map(|(code, _)| *code).collect();
        codes.sort_unstable();
        codes.dedup();
        assert_eq!(codes.len(), HRESULT_NAMES.len(), "a code is named twice");
    }

    #[test]
    fn summary_lists_accepted_formats_and_counts_the_rest() {
        let all = candidates();
        let answer = |name: &str| match name {
            "PCM 2.0 16-bit" | "PCM 7.1 16-bit" | "AC-3" | "E-AC-3" => S_OK,
            "MAT 2.1 [iec 48k 7.1]" => 0x8007_0057u32 as i32,
            _ => AUDCLNT_E_UNSUPPORTED_FORMAT,
        };
        let mixed: Vec<_> = all.iter().map(|c| (*c, answer(c.name))).collect();
        assert_eq!(
            summary(&mixed),
            "Summary: bitstream accepted: AC-3, E-AC-3; PCM accepted: PCM 2.0 16-bit, PCM 7.1 16-bit; \
             15 of 20 rejected; other: E_INVALIDARG (0x80070057) x1\n"
        );
        let denied: Vec<_> = all.iter().map(|c| (*c, 0x8007_0005u32 as i32)).collect();
        assert_eq!(
            summary(&denied),
            "Summary: bitstream accepted: none; PCM accepted: none; 0 of 20 rejected; \
             other: E_ACCESSDENIED (0x80070005) x20\n"
        );
        assert_eq!(
            not_probed("ActivateAudioInterfaceAsync result", 0x8007_0005u32 as i32),
            "Summary: not probed (E_ACCESSDENIED (0x80070005) at ActivateAudioInterfaceAsync result)\n"
        );
    }

    /// The probe must never take the endpoint: no client call beyond the questions it asks.
    #[test]
    fn the_probe_never_initializes_or_starts_a_client() {
        let source = include_str!("audio_probe.rs");
        for call in [
            concat!(".Initia", "lize("),
            concat!(".Sta", "rt("),
            concat!(".GetSer", "vice("),
            concat!("InitializeShared", "AudioStream("),
        ] {
            assert!(!source.contains(call), "audio_probe.rs calls {call}");
        }
    }
}
