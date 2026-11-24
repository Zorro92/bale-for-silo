# Audio passthrough

Status: **groundwork, nothing claimed** (2026-11-24). Bale still declares no `audio_passthrough_codecs`
(docs/player-plan.md); AC-3/E-AC-3 reach the console's own Dolby decoder and everything else is decoded to PCM.
Passthrough means handing a receiver the encoded stream as an IEC 61937 bitstream, which on Xbox needs the
console's "Allow passthrough" setting for media apps (since May 2021) and an audio endpoint that takes the
format in exclusive mode. Nothing here has been measured on the console yet; the owner has no receiver so far.

## Plan (owner, 2026-11-24: "get as much of the plumbing in as possible to future proof")

1. **Lossless decode to PCM** for TrueHD and DTS-HD MA: in place (FFmpeg decode, `software_decode.rs`, up to 8 channels).
2. **Capability probe:** below.
3. **IEC 61937 packers:** below.
4. **AC-3/E-AC-3 passthrough through Media Foundation** with the console's "Allow passthrough" (the route Plex and
   Disney+ use): to check with the owner at the TV (guide indicator).
5. **Bale's own exclusive-mode WASAPI output** fed by the packers, with the video following its audio clock, behind
   a debug switch; provable with AC-3 into a TV, TrueHD/DTS-HD then wait for a receiver.
6. **Claims:** `audio_passthrough_codecs` only from probe evidence.

References read (never copied): VLC `winstore.c`, `wasapi.c`, `tospdif.c` (LGPL 2.1+); Google Cobalt
`starboard/shared/uwp` (Apache-2.0; AC-3/E-AC-3 only, audio clock, seek and preroll handling); FFmpeg `spdifenc`
(LGPL); Microsoft's "Representing Formats for IEC 61937 Transmissions". Kodi (GPL) is not read. Real-app
behaviour (Plex forum): passthrough only on direct play; A/V desync after start and stutter at high bitrates are the
known hard parts.

## Capability probe

`scripts/bale-debug.sh audio-probe` (debug builds only: `GET /audio-probe`) answers one question: which encoded
formats would the console's default audio endpoint accept as an IEC 61937 bitstream **right now**, with the
current TV or receiver, HDMI audio settings and passthrough setting? Run it on any page, also during playback.

### What it does

bale_gpu does the work (`native/bale-gpu/src/audio_probe.rs`, export `bale_gpu_audio_probe`); the C# side
(`Services/DebugAudioProbe.cs`) calls it from a thread-pool thread and adds the spatial sound configuration.

1. Gets an `IAudioClient` the UWP way: `MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)`, then
   `ActivateAudioInterfaceAsync` with `IID_IAudioClient`. Microsoft lists a render endpoint with `IAudioClient` as an
   activation that is safe off the UI thread; the probe joins the MTA and waits at most 2 s for the completion
   callback (the handler is agile, as the API requires). A timeout or failed step is reported by step name and
   HRESULT.
2. `IAudioClient2::SetClientProperties` with `AudioCategory_Movie` and offload off, the category a film player uses.
3. Reports the shared-mode mix format (`GetMixFormat`): layout, channels, rate, bits, channel mask, subtype.
4. Asks `IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, format, NULL)` for every candidate below.

It **never calls `Initialize` or `Start`**: the client is only asked, then released before the report returns, so
the probe cannot take the endpoint from Media Foundation while a film plays. A Rust test fails if
`audio_probe.rs` ever gains such a call. `mmdevapi.dll` is loaded by the app (`NativeLibrary.TryLoad`) and passed
in; bale_gpu finds `ActivateAudioInterfaceAsync` with `GetProcAddress` and never imports the DLL (CI fails if
`dumpbin /dependents` lists it), like `d3d12.dll`, so no console can lose `bale_gpu.dll` over it.

Resources: no audio engine buffers (nothing is initialized), one COM client, a 16 KiB report and one blocked pool
thread for at most about 2 s; no GPU. None of the measured console limits (docs/resource-budget.md) is touched,
and only one probe runs at a time (a second request gets HTTP 409).

### Formats asked

IEC 61937 always travels as 16-bit IEC 60958 words: AC-3 and DTS on one line (2 channels) at the content rate,
E-AC-3 on one line at four times it (192 kHz), DTS-HD and TrueHD (Dolby MAT) as high bit rate audio on eight
lines at 192 kHz (a 768 kHz IEC 60958 frame rate). Each codec is asked in up to three layouts, because stacks
differ in what they expect:

- `ext`: `WAVEFORMATEXTENSIBLE` (`cbSize` 22), `wFormatTag` `WAVE_FORMAT_EXTENSIBLE`, the subtype GUID and the
  link's speaker mask (stereo `0x3`, or 7.1 surround `0x63F` for eight lines).
- `iec`: `WAVEFORMATEXTENSIBLE_IEC61937` (`cbSize` 34), which adds the decoded content's sample rate and channel
  count (`dwAverageBytesPerSec` 0, "ignored for this format"). Two rows are Microsoft's documented examples
  verbatim ("Representing Formats for IEC 61937 Transmissions"): E-AC-3 with 48 kHz 5.1 content and the back
  5.1 mask `0x3F`, TrueHD (MAT 1.0) with 96 kHz content and the wide 7.1 mask `0xFF`. The `768k` rows put
  768000 in `dwEncodedSamplesPerSec`, as some stacks do.
- `tag`: the legacy `WAVEFORMATEX` tags `WAVE_FORMAT_DOLBY_AC3_SPDIF` (0x0092) and `WAVE_FORMAT_DTS` (0x0008).

| Row | Layout | Link | Subtype (ksmedia.h) | Decoded content |
|---|---|---|---|---|
| PCM 2.0 16-bit | ext | 2 ch 48 kHz 16-bit | PCM | (context) |
| PCM 7.1 16-bit | ext | 8 ch 48 kHz 16-bit | PCM | (context) |
| PCM 7.1 24-bit in 32 | ext | 8 ch 48 kHz 32-bit, 24 valid | PCM | (context) |
| PCM 7.1 24-bit packed | ext | 8 ch 48 kHz 24-bit | PCM | (context) |
| AC-3 | ext | 2 ch 48 kHz | IEC61937_DOLBY_DIGITAL | |
| AC-3 [tag] | tag 0x0092 | 2 ch 48 kHz | | |
| E-AC-3 | ext | 2 ch 192 kHz | IEC61937_DOLBY_DIGITAL_PLUS | |
| E-AC-3 [iec 48k 5.1] | iec | 2 ch 192 kHz, mask 0x3F | IEC61937_DOLBY_DIGITAL_PLUS | 48 kHz 6 ch |
| E-AC-3 Atmos [iec 48k 5.1] | iec | 2 ch 192 kHz, mask 0x3F | IEC61937_DOLBY_DIGITAL_PLUS_ATMOS | 48 kHz 6 ch |
| DTS | ext | 2 ch 48 kHz | IEC61937_DTS | |
| DTS [tag] | tag 0x0008 | 2 ch 48 kHz | | |
| DTS-HD | ext | 8 ch 192 kHz | IEC61937_DTS_HD | |
| DTS-HD [iec 48k 7.1] | iec | 8 ch 192 kHz | IEC61937_DTS_HD | 48 kHz 8 ch |
| DTS-HD [iec 768k] | iec | 8 ch 192 kHz | IEC61937_DTS_HD | 768000 "Hz" 8 ch |
| TrueHD | ext | 8 ch 192 kHz | IEC61937_DOLBY_MLP | |
| TrueHD [iec 48k 7.1] | iec | 8 ch 192 kHz | IEC61937_DOLBY_MLP | 48 kHz 8 ch |
| TrueHD [iec 768k] | iec | 8 ch 192 kHz | IEC61937_DOLBY_MLP | 768000 "Hz" 8 ch |
| TrueHD [iec 96k wide 7.1] | iec | 8 ch 192 kHz, mask 0xFF | IEC61937_DOLBY_MLP | 96 kHz 8 ch |
| MAT 2.0 [iec 48k 7.1] | iec | 8 ch 192 kHz | IEC61937_DOLBY_MAT20 | 48 kHz 8 ch |
| MAT 2.1 [iec 48k 7.1] | iec | 8 ch 192 kHz | IEC61937_DOLBY_MAT21 | 48 kHz 8 ch |

The GUIDs are ksmedia.h's strings verbatim (Windows SDK 10.0.26100). The Rust tests pin this list line by line,
the two Microsoft examples field by field and the 52-byte packed layout.

### Reading the report

One line per row: `name: layout link mask subtype[, decoded content] -> result`, for example
`AC-3: ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL -> AUDCLNT_E_UNSUPPORTED_FORMAT (rejected)`
(the format of a line, not a measurement). Results use their header names:

- `S_OK (accepted)`: the endpoint would open that format in exclusive mode now.
- `AUDCLNT_E_UNSUPPORTED_FORMAT (rejected)`: it would not (format, rate or channel count not offered).
- Anything else (for example `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED`, `E_ACCESSDENIED`, `AUDCLNT_E_DEVICE_INVALIDATED`)
  says exclusive mode itself is unavailable or the endpoint changed; the summary counts these separately.

The last report line, `Summary: bitstream accepted: ...; PCM accepted: ...; N of 20 rejected; other: ...`, is
also written to bale.log as `Debug audio-probe: ...`. After it comes the spatial sound configuration of the same
endpoint (`SpatialAudioDeviceConfiguration`: supported, active and default format, and which of Windows Sonic,
Dolby Atmos and DTS:X it offers). An active "Dolby Atmos for home theater" or "DTS:X for home theater" means the
console bitstreams its own mix; it is context, not evidence that an app's bitstream is accepted.

### Not known yet

- Whether exclusive mode is open to a UWP app on Xbox at all, and how "Allow passthrough" changes the answers
  (expected: rejections or `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` while it is off; unverified).
- Whether the answers follow the receiver's or TV's reported formats (HDMI EDID) or only the console's HDMI audio
  setting, and which layout (`ext`, `iec`, `tag`) the Xbox stack expects.
- Whether `SetClientProperties` (Movie category) changes any answer. It is applied before every question.

Worth running: once without a receiver (the TV alone), with "Allow passthrough" off and on, and again when a
receiver is connected. The accepted layout decides how the IEC 61937 packers' output will be handed to the
endpoint.

### First console run (2026-11-24, Bale 0.1.754, TV over HDMI, console "Allow passthrough" off)

Activation through `ActivateAudioInterfaceAsync` worked (6 ms) after loading `mmdevapi.dll` from System32 (a bare-name
load fails in the UWP package). Mix format: 2 ch, 48 kHz, 32-bit float. **All 20 exclusive-mode checks, PCM included,
returned `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` (0x8889000E):** with "Allow passthrough" off the console refuses exclusive
mode to apps altogether. Next: the same probe with Settings › General › Volume & audio output › Advanced › "Allow
passthrough" on (owner at the console).

## IEC 61937 packers

`native/bale-core/src/iec61937.rs` (pure Rust, tested on Linux) wraps compressed access units in the
IEC 61937 bitstream a receiver decodes: the bytes a WASAPI exclusive-mode client writes as 16-bit
little-endian PCM in a `WAVEFORMATEXTENSIBLE_IEC61937` format. Nothing calls it yet: no renderer, no C
ABI, no capability claim.

| Format | Packer | Burst (IEC 61937 data type) | Carrier, 16-bit | `KSDATAFORMAT_SUBTYPE_…` | Carrier frames a burst |
|---|---|---|---|---|---|
| AC-3 | `Ac3Packer` | -3 type 1: one sync frame | 2 ch at the stream rate (48 kHz) | `IEC61937_DOLBY_DIGITAL` | 1536 |
| E-AC-3, Atmos included | `Eac3Packer` | -3 type 21: frames for six audio blocks (1536 samples) | 2 ch at 4× the stream rate (192 kHz) | `IEC61937_DOLBY_DIGITAL_PLUS` | 6144 |
| DTS core | `DtsPacker` | -5 types 11/12/13 by 512/1024/2048 samples; a DTS-HD extension is dropped | 2 ch at the core rate (48 kHz) | `IEC61937_DTS` | 512/1024/2048 |
| DTS-HD MA/HRA | `DtsHdPacker` (`DtsHdLink::HighBitrate`) | -5 type 17: 12-byte DTS-HD header, core, extension | 8 ch at 4× the core rate (192 kHz, HBR) | `IEC61937_DTS_HD` | 2048 for a 512-sample core |
| TrueHD / MLP, Atmos included | `TrueHdPacker` | -9 type 22: one MAT frame, 24 access units | 8 ch at 192 kHz (176.4 kHz for 44.1 kHz streams) | `IEC61937_DOLBY_MLP` | 3840 |

Use: `Format::carrier(sample_rate)` gives the device format from the demuxer's rate before the first
unit; `push(unit)` takes one access unit as FFmpeg's demuxer delivers it and returns the bursts it
completed (`Burst { data, frames, carrier }`); `reset()` on a seek or discontinuity; `flush()` at the end
of the stream. A rejected unit returns an `Error` and changes nothing. Every burst plays for
`frames / carrier.sample_rate`, so the audio clock is the carrier frames written since the last reset
plus the timestamp of the first unit pushed after it. `DtsHdLink::Stereo` (2 ch at 192 kHz) exists for
links without HBR; DTS-HD frames that don't fit it fall back to the core for a minute, as FFmpeg does.

Sized for docs/resource-budget.md's audio line (< 0.25 core, ~20 MiB): a packer holds at most one MAT
frame (60 KiB) or one E-AC-3 burst's frames (24 KiB), and each burst is one allocation of its period
(at most 64 KiB, 43 ms of audio at the longest). Work is one copy and byte swap per output byte, at
most 3.1 MB/s (HBR), on the CPU; nothing needs the GPU.

**Verification.** `native/bale-core/tests/iec61937_golden.rs` compares every packer with FFmpeg 9.0.2's
`spdif` muxer byte for byte; the fixtures and how they were made are in
`native/bale-core/tests/fixtures/iec61937/README.md`. Byte-identical on every fixture:

- AC-3 (48 and 44.1 kHz), E-AC-3 (6-block from FFmpeg's encoder; synthetic 1-block 7.1 with dependent
  frames and 3-block), DTS core (types I-III, 16-bit big- and little-endian, 14-bit both ways, a
  no-preamble DTS CD-style stream, core taken from DTS-HD), TrueHD (48 and 96 kHz), MLP, and TrueHD
  with timing jumps: real FFmpeg encodes except where marked synthetic.
- DTS-HD over HBR (`-dtshd_rate 768000`) and over the stereo link (`-dtshd_rate 192000`, with the
  core-only fallback): synthetic, since no DTS-HD clip exists locally and FFmpeg can't encode one. The
  cores are real, the extension substreams have real headers and filler, so the IEC 61937-5 framing is
  checked exactly but no real Master Audio stream was packed.

Unit tests cover what fixtures can't: errors, AC-3 frame sizes at every rate, the DTS-HD fallback's
length, TrueHD units landing on their 2560-byte slots, reset and flush.

Where the standards leave room, FFmpeg's choice is kept: Pd rounding, the DTS-HD Pd alignment
(`Pd % 16 == 8`), the MAT code positions and TrueHD's padding limit (half a frame). Where FFmpeg's
demuxers never produce the input, the packers do more than its muxer: a unit holding several frames is
split and trailing bytes after the last whole frame are dropped; E-AC-3 counts program 0's audio blocks
(FFmpeg counts units by the first frame's block count, the same for every real stream); TrueHD units
under 10 bytes are accepted; `flush` sends the partly filled E-AC-3 burst or MAT frame (FFmpeg drops it).
Rates IEC 61937 doesn't carry (below 32 kHz, E-AC-3's reduced rates) are refused.

Open: the renderer (WASAPI exclusive mode or Media Foundation's audio renderer with these subtypes),
receiver capability checks, a C ABI, and verification on a real receiver.
