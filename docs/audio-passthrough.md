# Audio passthrough

Status: **groundwork, nothing claimed** (2026-11-24). Bale still declares no `audio_passthrough_codecs`
(docs/player-plan.md); AC-3/E-AC-3 reach the console's own Dolby decoder and everything else is decoded to PCM.
Passthrough means handing a receiver the encoded stream as an IEC 61937 bitstream, which on Xbox needs the
console's "Allow passthrough" setting for media apps (since May 2021) and an audio endpoint that takes the
format in exclusive mode. The capability probe has run on the console (below); the exclusive output's first run
stopped at `Initialize` ("Exclusive output", "First console run"). The owner has no receiver so far.

## Plan (owner, 2026-11-24: "get as much of the plumbing in as possible to future proof")

1. **Lossless decode to PCM** for TrueHD and DTS-HD MA: in place (FFmpeg decode, `software_decode.rs`, up to 8 channels).
2. **Capability probe:** below.
3. **IEC 61937 packers:** below.
4. **AC-3/E-AC-3 passthrough through Media Foundation** with the console's "Allow passthrough" (the route Plex and
   Disney+ use): to check with the owner at the TV (guide indicator).
5. **Bale's own exclusive-mode WASAPI output** fed by the packers (and decoded multichannel PCM), behind a debug
   switch; provable with AC-3 into a TV, TrueHD/DTS-HD then wait for a receiver. The audio follows the video
   (2026-11-24): IMFMediaEngine stays the master clock and the output drops or pads to match it. Step 1, the output
   itself with a debug test, is in place: "Exclusive output" below.
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

Same day, two more runs:
- **"Allow passthrough" on, a headset connected:** still all 20 `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED`. With a headset
  connected the console routes all audio to it and locks the HDMI settings ("headset is connected, changing HDMI output
  is temporarily disabled"); the default render endpoint is then the headset, which never offers exclusive mode.
- **Headset unplugged, HDMI to the TV ("Stereo uncompressed" is the only speaker option the TV offers), "Allow
  passthrough" on:** exclusive mode is **allowed**. Accepted: `PCM 7.1 24-bit in 32` (8 ch 48 kHz, 32-bit container,
  24 valid bits), the console's fixed exclusive format. Rejected (`AUDCLNT_E_UNSUPPORTED_FORMAT`): every other PCM
  layout and every bitstream (AC-3, E-AC-3, DTS, DTS-HD, TrueHD, MAT), as expected for a TV that takes only stereo PCM.

So an app can open exclusive mode on Xbox when "Allow passthrough" is on and no headset is connected; which bitstreams
are accepted then follows the HDMI device. The future passthrough path must re-check per playback (a headset can be
plugged in at any time) and fall back to decoded audio.

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

Open: feeding the exclusive output ("Exclusive output", next steps), receiver capability checks, a C ABI for the
packers, and verification on a real receiver.

## Exclusive output

Step 1 of plan item 5, debug plumbing only: bale_gpu's exclusive-mode WASAPI output (`native/bale-gpu/src/audio_out/`),
C ABI 1 in `native/bale-gpu/include/bale_audio_out.h` (`bale_audio_out_*`, additive: the bale_gpu ABI version stays;
CI requires the exports and fails if bale_gpu.dll imports mmdevapi.dll or avrt.dll). No release path creates an output
and no claim changes. The FFmpegInteropX fork will be the feeder; the app owns the output.

### What it does

- **Open** (`bale_audio_out_create`): the default render endpoint the probe's way (MediaDevice default render id, then
  `ActivateAudioInterfaceAsync` through the app's `mmdevapi.dll`, which bale_gpu pins while the output lives but never
  imports), `AudioCategory_Movie`, `IsFormatSupported(EXCLUSIVE)`, then `Initialize(EXCLUSIVE, EVENTCALLBACK)` with
  buffer duration = periodicity = the device's default period. `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` takes Microsoft's
  documented recipe (`GetBufferSize`, a new client, the aligned duration). When that `Initialize` answers
  `AUDCLNT_E_UNSUPPORTED_FORMAT` or `AUDCLNT_E_OFFLOAD_MODE_ONLY` (the console, below), create goes on to offload, each
  try a new client with `bIsOffload` set and the buffer fitted into `GetBufferSizeLimits`, event-driven: exclusive with
  offload, then (PCM only, since a shared engine may mix) shared with offload, whose render loop fills what the engine
  played (`GetCurrentPadding`) instead of the whole buffer. `BaleAudioOutOpenInfo.path` and `BaleAudioOutStats.path` say
  which path opened (0 exclusive, 1 exclusive offload, 2 shared offload; fields that were reserved), `offload_hr` what
  the exclusive offload try answered. The plain path stays first, for other platforms. A refusal returns its HRESULT
  and the step (`BaleAudioOutOpenInfo`), so the caller falls back: `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` (headset
  connected, or "Allow passthrough" off), `AUDCLNT_E_UNSUPPORTED_FORMAT`, `AUDCLNT_E_DEVICE_IN_USE`. Formats: integer PCM (1-8
  channels, 16/24/32-bit containers) or IEC 61937 (2 or 8 lines of 16-bit words at a carrier rate, the probe's five
  subtypes), as `WAVEFORMATEXTENSIBLE`, or `WAVEFORMATEXTENSIBLE_IEC61937` (`cbSize` 34) when a decoded rate is given;
  the Rust tests check them byte for byte against the probe's rows. The console's TV path takes only PCM 7.1 48 kHz,
  24 bits in 32, mask `0x63F`.
- **Ids**: 1-65535 (never 0) in a thread-safe table, so the fork takes the id through an FFmpegOptions int; an id comes
  back only after the counter has gone round. Every call is thread-safe; only create (activations, at most ~12 s) and
  destroy (joining the thread, at most 3 s) wait, so neither belongs on the UI thread.
- **Render thread**, one per output, makes every COM call: it waits for the buffer event (or a control wake), fills one
  period from the ring and reads `IAudioClock` each period. A ring that runs short is filled up with silence (counted:
  `underruns`, `silence_frames`). MMCSS "Pro Audio" when `avrt.dll` is already in the process (the app loads it; bale_gpu
  only looks for a loaded copy), else normal priority. No buffer event for 2 s while running is a stall: status
  `FAILED` with `HRESULT_FROM_WIN32(ERROR_TIMEOUT)`, as in Microsoft's exclusive-mode sample. `AUDCLNT_E_DEVICE_INVALIDATED`
  or `AUDCLNT_E_RESOURCES_INVALIDATED` (an output change) is `DEVICE_LOST`. Either way writes return
  `BALE_AUDIO_OUT_WRITE_CLOSED`, control calls return the error, and the caller falls back and destroys the output.
- **Writes never block**: a bounded ring (500 ms by default, 50-1000 ms at create, at least two device buffers) takes
  whole frames until full and then answers 0, and the feeder waits a few milliseconds. That bound keeps the feeder from
  reading the file far ahead of the video. Each write's pts times its first frame; a pts within 2 ms of the counted one
  continues the timeline (container timestamps are often rounded), a bigger jump starts a new one there.
- **Start / pause / flush** are idempotent and callable from any thread. Flush is the seek: it drops the ring at once
  (frames written after it stay), then the render thread stops and resets the stream (its clock restarts at 0) and starts
  it again if it was playing.
- **Sync** (`bale_audio_out_sync(id, video_position_100ns, qpc_ticks)`, the brief's "set reference clock"): the app sends
  the video position and the QueryPerformanceCounter value at the same instant. At its next period the render thread
  extrapolates its `IAudioClock` position to that QPC time and reports pts-at-the-device minus video position as
  `sync_error_100ns` (positive = audio ahead). It decides on the error the next frame handed to the device will have
  when heard, which already includes the silence still queued and the corrections still pending: an underrun's silence
  is caught up while it is still queued, and no correction is counted twice. Beyond 20 ms, PCM is corrected with whole
  frames: silence when the audio is ahead, dropped queued frames when it is behind, at most 1 s per reference; errors
  beyond 10 s are taken for a stale reference (a seek in flight) and ignored. IEC 61937 is only measured for now:
  a correction inside a data burst would break it.

The platform-independent parts (format rules, ring, timeline, sync controller, the per-period fill and a simulated
device: in sync, ahead, behind, an underrun, a bitstream) are unit tested on Linux; the header is compiled as C and C++
against the Rust layout. The COM side compiles and lints for `x86_64-pc-windows-msvc` and has not run yet.

Sized for docs/resource-budget.md's audio line (< 0.25 core, ~20 MiB) against the measured console limits (3328 MiB
app memory, 4 shared cores, CPU copies 3.6-4x slower input-idle): the ring is at most 1000 ms (the 500 ms default is
750 KiB for 7.1 32-bit 48 kHz PCM, 1.46 MiB for an 8-line 192 kHz IEC 61937 link), the device buffers are the audio
engine's, and one thread wakes once a period (10 ms by default) to copy one period (15 KiB for 7.1 PCM): about 1.5 MB/s
of copies, a small fraction of one core even when input-idle. No GPU.

### Debug test

`scripts/bale-debug.sh audio-out-test [seconds] [freq] [channels=2|6|8] [uisounds=off]` (debug builds only:
`GET /audio-out-test?seconds=5&tone=440&channels=8`; 1-30 s, default 5; 20-20000 Hz, default 440; 2, 6 or 8 channels of
24 bits in 32 at 48 kHz, default 8, the console's format) opens the output (with the offload fallbacks above), fills the
ring, starts it and plays a sine on front left and right (-18 dBFS; the other channels silent) for the given time. It feeds 10 ms chunks
with their pts as the fork's feeder will (waiting on "full", resending the frames not taken) and sends a sync reference
every 100 ms from a wall-clock "video" that starts at Start. It is refused with 409 while a film plays (Media Foundation
holds the endpoint) or another media probe runs, and it always destroys the output.

The report starts with the app's XAML UI sound state (`ElementSoundPlayer.State`; `uisounds=off` switches them off
for the run and restores them afterwards, as they may hold a shared stream on the endpoint). Then `Create:` (HRESULT
and the step it stopped at; activation, Movie category, IsFormatSupported, Initialize, a re-aligned Initialize, the
offload tries and the path), `Device:` (default and minimum period, the initialized period, buffer frames, ring, MMCSS),
`Prefill:`, `Start:`, one line a second (device clock, queued ms, underruns, sync error and corrections), `Pause:`,
`Result:` (frames fed and rendered, the device clock against wall time since Start, which includes the start latency,
the clock drift against QPC in ppm between the first and last reading, underruns, sync and corrections, final status)
and `Summary:` (also in bale.log as `Debug audio-out-test: ...`), then `Destroy:`. The first sync errors show the start
latency (the wall clock starts before the first sample sounds), so a first correction of a few tens of milliseconds is
expected.

### First console run (2026-11-24, Bale 9e216fe, TV over HDMI, "Allow passthrough" on, no headset)

`audio-out-test 10 440`: activation `S_OK` in 10 ms, Movie category `S_OK`, `IsFormatSupported(EXCLUSIVE)` of the
console's format (PCM 8 ch 48 kHz, 24 bits in 32, mask `0x63F`) `S_OK`, but **`Initialize` (exclusive, event-driven,
buffer = periodicity = the default period) returned `AUDCLNT_E_UNSUPPORTED_FORMAT` (0x88890008)**. Device period:
default 10 ms, minimum 3 ms. Nothing played. So `IsFormatSupported` alone is no evidence that an exclusive stream
opens on Xbox; the probe's answers (above) say what is offered, not what initializes.

### Initialize variants

`scripts/bale-debug.sh audio-out-test variants [uisounds=off]` (debug builds only: `GET /audio-out-test?variants=1`;
bale_gpu `bale_audio_out_init_variants`, `native/bale-gpu/src/audio_out/variants.rs`) narrows that down. For each row
it activates a fresh `IAudioClient`, applies the row's client properties, asks `IsFormatSupported` and calls
`Initialize` in the row's share mode, records the answers (and on success `GetBufferSize` and `GetStreamLatency`),
then releases the client and waits 150 ms before the next row, so a just-released client cannot make the next one look
in use. It never calls `Start` or asks for a render client (a Rust test pins that), so nothing plays; an initialized
client holds the endpoint only until it is released, milliseconds later. `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` runs
Microsoft's re-align recipe on a new client and `AUDCLNT_E_DEVICE_IN_USE` tries once more on a new client 500 ms
later; the line reports both answers. A 2 s activation timeout stops the run. It is refused with 409 while a film plays
or another media probe runs; the report starts with the XAML UI sound state (as the tone test's), and the summary line
goes to bale.log as `Debug audio-out-test variants: ...`.

Before the rows, one client that is only asked reports the context: the shared mix format, the device periods,
whether `IAudioClient3` exists, `IAudioClient2::IsOffloadCapable` for Movie and Media, `IsFormatSupported(SHARED)` of
the output's format with the closest match it offers, and `GetBufferSizeLimits` for it without offload, event-driven
and polling. Then one line per row, `name: init; client properties; format -> answers`. The rows (all with the
output's format and the Movie category unless named otherwise):

| Rows | What varies |
|---|---|
| offload Movie / Media (first) | `SetClientProperties` with `bIsOffload` set, `GetBufferSizeLimits` after it (the line shows the limits and the buffer and periodicity passed), then: exclusive event-driven (buffer = periodicity = the default period, fitted into the limits) and exclusive polling (200 ms fitted, periodicity 0) for 7.1, 5.1 and 2.0 24-in-32, 2.0 16-bit, AC-3 and E-AC-3; and shared mode (`IsFormatSupported(SHARED)` and `Initialize(SHARED)`: event-driven with a 20 ms buffer, and polling with 200 ms, both fitted, periodicity 0) for the mix format and 7.1 24-in-32 |
| a-g | `Initialize` flags and timing: (a) event-driven, buffer = periodicity = default period (the output's own call); (b) event-driven at the minimum period; (c) polling, 200 ms buffer, periodicity 0; (d) polling, buffer = periodicity = default; (e) (a) plus `AUDCLNT_STREAMFLAGS_NOPERSIST`; (f) event-driven with 0/0; (g) (a) without asking `IsFormatSupported` first |
| p1-p5 | client properties: (p1) plain `IAudioClient`, no properties; (p2) `AudioCategory_Other`; (p3) `AudioCategory_Media`; (p4) Movie with `AUDCLNT_STREAMOPTIONS_RAW` (the line shows it if refused); (p5) polling without properties |
| formats, each event-driven (as a) and polling (as c) | PCM 7.1 with 32 valid bits; 5.1 24-in-32 (mask `0x3F`); 2.0 16-bit; 2.0 24-in-32; 7.1 16-bit; 2.0 16-bit as a plain `WAVEFORMATEX`; 7.1 32-bit float; the shared mix format as `GetMixFormat` returns it; the closest match `IsFormatSupported(SHARED)` offers; AC-3 and E-AC-3 as IEC 61937 (`WAVEFORMATEXTENSIBLE`), and E-AC-3 as Microsoft's `WAVEFORMATEXTENSIBLE_IEC61937` example |

The summary names the rows that initialized and counts the last `Initialize` answers of the others. The AC-3 and
E-AC-3 rows are expected to be refused on this TV (it takes only stereo PCM); they show whether a bitstream goes the
same way through `Initialize`.

### Second console run: variants (2026-11-24, Bale ea84c7b, same setup)

All 36 rows of the first variants list (before the offload rows) were refused: `Initialize` `AUDCLNT_E_UNSUPPORTED_FORMAT`
x28, `AUDCLNT_E_DEVICE_IN_USE` x8. Context: mix format 2 ch 48 kHz 32-bit float; device period 10 ms default, 3 ms
minimum; `IAudioClient3` available; `IsFormatSupported(SHARED)` of the 7.1 format `S_FALSE` with the float stereo mix
as closest match; `GetBufferSizeLimits` (no offload, event-driven and polling) `AUDCLNT_E_OFFLOAD_MODE_ONLY`.

- `IsFormatSupported(EXCLUSIVE)` accepted 24-in-32 PCM in 7.1, 5.1 and 2.0 (the probe had not asked 5.1 or 2.0 in that
  layout) and refused 16-bit, 32 valid bits, float, the mix format and the bitstreams.
- `AUDCLNT_E_DEVICE_IN_USE` came exactly for the stereo and 5.1 PCM rows (including 2.0 16-bit, which
  `IsFormatSupported` had refused); 7.1, float, the mix format and the bitstreams got `AUDCLNT_E_UNSUPPORTED_FORMAT`.
  No earlier row had initialized, so the cause was not one of the test's own clients. Microsoft documents
  `AUDCLNT_E_DEVICE_IN_USE` as "the device is being used in shared mode and the caller asked to use the device in
  exclusive mode": likely the console's own shared audio, or Bale's XAML UI sounds (`ElementSoundPlayer`, on by default
  on Xbox), which the report now shows and `uisounds=off` switches off for a run.
- `AUDCLNT_E_OFFLOAD_MODE_ONLY` from `GetBufferSizeLimits` says that call answers only for offload clients; the offload
  rows (now first) and create's offload fallbacks test whether this endpoint takes offload streams, exclusive or shared.

### Not known yet (needs the console)

- Whether an offload `Initialize` (exclusive or shared) succeeds where the plain one did not (the offload rows above,
  and the tone test's path), and whether `AUDCLNT_E_DEVICE_IN_USE` goes away with the UI sounds off.
- Whether the app can load `avrt.dll` and join MMCSS, and how `IAudioClock` reports there (its `GetFrequency` units, the
  drift against QPC).
- What plugging in a headset does to an open exclusive stream (invalidated, silent, or still on HDMI; the output is
  bound to the endpoint it opened).
- Whether the TV plays the 7.1 PCM (it reports stereo only; the console's fixed exclusive format is 7.1), and whether
  an exclusive stream mutes the console's own sounds.
- What suspending the app does to the stream; the app must destroy the output on suspend once it uses one.

### Next steps (not done)

1. **Fork** (FFmpegInteropX, as the fork's thread described it): a debug key with the id (`bale_audio_out=<id>`) and the
   audio track chosen at open (`bale_audio_out_stream=<index>`; switching tracks means reopening). Its own audio feeder
   thread decodes, converts with swr to signed 32-bit 8 ch 48 kHz in WASAPI 7.1 order (FL FR FC LFE BL BR SL SR, the
   24-bit value in the top three bytes), writes with pts in 100 ns on the video's timeline, waits on "full", and calls
   flush and writes from the new pts after a seek or stream switch. `bale_audio_out_format` tells it the format from
   the id alone.
2. **App**: create the output before the open and fall back to Media Foundation's audio on any failure; pass the id;
   start and pause it with the engine; call `bale_audio_out_sync` every 100-250 ms while playing (not during a seek)
   with IMFMediaEngine's current time and the QPC value read with it; destroy it on stop, suspend and track changes.
   Check exclusive mode per playback (a headset can come at any time) and fall back on `DEVICE_LOST`.
3. **Passthrough** later through the same feeder: codec id, access units and pts into the bale-core packers, whose
   `Format::carrier` gives the `BaleAudioFormat` (kind IEC 61937, the subtype, the carrier's channels and rate), with
   sync corrections in whole bursts (the packer's repetition period as the controller's granularity).
4. **Claims** (`audio_passthrough_codecs`) only from probe evidence and a stream verified on a receiver.
