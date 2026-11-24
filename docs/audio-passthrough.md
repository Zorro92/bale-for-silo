# Audio passthrough

Status: **groundwork, nothing claimed** (2026-11-24). Bale still declares no `audio_passthrough_codecs`
(docs/player-plan.md); AC-3/E-AC-3 reach the console's own Dolby decoder and everything else is decoded to PCM.
Passthrough means handing a receiver the encoded stream as an IEC 61937 bitstream, which on Xbox needs the
console's "Allow passthrough" setting for media apps (since May 2021) and an audio endpoint that takes the
format in exclusive mode. Nothing here has been measured on the console yet; the owner has no receiver so far.

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
