# Video player plan

Bale has a bare player with session recovery and debug stats. This is its phased plan. It copies the
official Android TV player (`silo-android/androidTvApp/.../tv/ui/screens/player/`, protocol in
`shared/.../model/playback/PlaybackProtocolV3.kt`, `network/apiv2/PlaybackV2Api.kt`,
`repository/SequencedPlayback.kt`). The tvOS docs under `silo-apple/docs/tvos-player/` are marked
historical; use them only for intent.

## Engine

The renderer is Windows' `MediaPlayer` + `MediaPlayerElement`. Ordinary direct playback uses
`MediaSource.CreateFromStream` and registered system decoders. Production FFmpegInteropX is the
fallback for allowlisted AV1 video or selected audio that needs bundled decoding; HLS always uses
`AdaptiveMediaSource`. Passthrough is still unclaimed until verified with a receiver.

Server behavior: any video transcode re-encodes audio to AAC (max 6 ch); silo-server's
`planVideoTranscodeV3` always adds `audio_to_aac`. Audio is only copied when the video is copied
(direct play or remux). With the production AV1 fallback, a supported AV1 + EAC3 5.1 file can
stay direct. When that fallback is disabled, fails, or exceeds the measured bounds, server video
transcode produces HEVC + AAC 5.1 instead.

Evidence so far (console, `bale-debug.sh caps`, build 0.1.200):

| | |
|---|---|
| Video decoders | H.264, HEVC, VP8/VP9, MPEG-2, VC-1. **AV1: none.** No Dolby Vision decoder. |
| Audio decoders | AAC, AC3, EAC3, FLAC, ALAC, Opus, MP3 (PCM needs none) |
| Passthrough only | TrueHD, DTS, DTS-HD (IEC-61937 converters, no decoder) |
| Output | Digital Output, 8 ch / 48 kHz float mix |
| Developer's display | 720p/1080p at 50/59.94 Hz, SDR only (no HDR10/DV modes reported); not a hardcoded client assumption |

`bale-debug.sh playtest suite` (0.1.201, Chromium's `bear-*` clips, muted `MediaPlayer`):

| Clip | Result |
|---|---|
| MP4 H.264 + AAC, MP4 HEVC (2 clips), MP4 AC3, MP4 EAC3 | **plays** |
| MP4 HEVC Main10 1280x720 (SDR and HDR10-tagged), MP4 HEVC 3840x2160 8-bit | **plays** (0.1.215). Bale claims HEVC 8 and 10-bit. |
| MP4 / WebM AV1 (8-bit and 10-bit) | **fails**: `UnsupportedSubtype`, MediaFailed 0xC00D5212 (no decoder). Definitive: MediaPlayer can't play AV1 here. |
| WebM VP8+Vorbis, VP9, VP9+Opus | opens, decoders report FullySupported, but never leaves Paused at 0 s. Unexplained; WebM is rare in libraries, so don't claim it. |
| MP4 FLAC, MP4 Opus | `UnsupportedSubtype`: MP4's `fLaC`/`Opus` sample entries aren't mapped, though the FLAC/Opus decoders exist. Retest inside MKV with real files. |

### GPU decode (`caps gpu`, D3D11 video device, build 0.1.223)

Series X: feature level 11_0 (0xB000), 11 profiles. H.264 (VLD_NOFGT, multiview), MPEG-2,
VC-1 and MPEG-4 Part 2 simple at 1080p; HEVC Main and Main10 (P010) up to 8K; VP9 profile 0
and profile 2 10-bit (P010) up to 4K. **No AV1 profile**: the GPU path doesn't help AV1 on this
console. AV1 needs software decode (dav1d).

`caps gpu` also reports expanded-resource (game-mode) status, console model, DXGI adapter memory
and software flags, and D3D12 app-mode availability: default-adapter LUID matching, maximum feature
level/shader model, compute-queue creation, and explicit non-software adapter device creation.
This probes hardware access, not video decode support or a new playback route.
Results (Series X, Bale 0.1.257.0, app mode, 2026-11-07): `HasExpandedResources` = no (not game
mode). DXGI lists the hardware adapter `SraKmd_arden` (VendorId 0x1414, 512 MiB dedicated, ~2.6 GiB
shared) and the software Basic Render Driver. Default `D3D12CreateDevice` at FL 11_0 succeeds on
the **hardware** adapter (LUID matches DXGI 0, not WARP): max feature level 11_0, shader model 6.4,
compute queue created. Explicit creation on DXGI 0 while the default device is alive returns
0x887A0006 (`DXGI_ERROR_DEVICE_HUNG`); use the default adapter. So D3D12 compute is available to
Bale without game mode, which is the prerequisite for a GPU AV1 path (e.g. av1-xbox-one's compute
decoder). D3D11 still exposes no AV1 decoder profile.

Still to test with real library files: TrueHD/DTS passthrough to a receiver.

The capability claims Bale sends the server must come from this kind of evidence, never from
guesses. `playtest` (debug only) checks actual playback, not just installed decoders.

**Decision: ship FFmpegInteropX as the production fallback.** By the project owner's rule,
software **video** decode is **AV1 only**. `SoftwareDecodePolicy.VideoCodecs` is an explicit,
owner-maintained allowlist; nothing else may be added without them. Unsupported non-AV1 video
goes to the server to transcode, never FFmpeg software or D3D11 video. The 0.1.230 Fight Club test
is the measured AV1 evidence: 8/10-bit, max 1920×1080, max 30 fps. **4K software AV1 is untested
and is not claimed.** Audio may use FFmpeg software whenever the system path cannot handle it.

Current production capability claims (`ConsoleEvidence.Default` plus runtime display detection, max audio channels **8**):

| Stream | Claims / engine | Bounds / limitations |
|---|---|---|
| H.264 / HEVC video | Media Foundation, hardware | 1920×1080, 60 fps, 25 Mbps; H.264 8-bit, HEVC 8/10-bit |
| AV1 video | dav1d, **non-hardware**; `software_video_decode_v1` opt-in | 8/10-bit, **1920×1080, 30 fps**; no 4K claim |
| System audio | `aac`, `ac3`, `eac3`, `mp3`, `pcm` | Retains the native direct path unless AV1 needs FFmpeg |
| FFmpeg fallback audio | `dts`, `dts_hd`, `truehd`, `flac`, `opus`, `vorbis`, `alac` | Decode to PCM, not passthrough; real-library console verification pending |
| Audio passthrough | None | Receiver verification pending |
| Display HDR | Runtime HDMI detection, HDR10 only | Exact evidence after a successful read; unknown/empty HDR on failure |
| HLS | System video/audio claims only | AdaptiveMediaSource cannot use FFmpeg fallbacks |
| Subtitles | None locally | Server burn-in; **TODO:** add FFmpeg subtitle fallback when Bale renders subtitles |

`PlaybackRoutePolicy.Decide` selects the pipeline separately for every mount. `original_http` /
progressive uses FFmpeg only for AV1 or the **selected** fallback audio codec. AV1 sets
`Video.VideoDecoderMode = ForceFFmpegSoftwareDecoder`; audio-only fallback sets
`ForceSystemDecoder`. Never use automatic or FFmpeg D3D11 video in real playback. The pinned
2.1.0 implementation can fall back even in ForceSystemDecoder for unsupported formats, so Bale
also validates all reported video decoder engines before mounting/reading frames.

All builds, including production (`BaleDebugServer=false`), apply the wrapper-fork defaults from
`SoftwareDecodeTuning` to software AV1: `bale_decode_ahead_frames=16` and `bale_sws_threads=4`.
`bale_dav1d_max_frame_delay=8` is set only above 1080p (width > 1920 or height > 1080). Route
width/height follow the video codec's plan selection: original delivery uses `plan.Source`; other
deliveries use `plan.EffectiveRecipe`, falling back to `plan.Source` for each missing dimension.
Unknown size leaves delay unset. Hardware/system video routes set none of the three keys.
`bale_decode_thread_priority` remains unset in production; only the debug `prio` overrides below
can emit it. This is decode tuning, not new 4K capability evidence or a verified performance improvement.
All builds also set `MediaStreamSource.BufferTime` to **2000 ms for software video**, using
`SoftwareDecodeTuning.DefaultBufferMs`; FFmpeg system-video routes retain the wrapper's zero default.
Console A/B tests increased queued decoded video from ~0.3 s to ~2.3 s without added late frames,
trading ~1 s of buffering at start/seek for the larger queue.

`Audio.SystemDecoderAAC` and `Audio.SystemDecoderMP3` are enabled. Upstream FFmpegInteropX
**2.1.0 and master only have `SystemDecoderAAC` / `SystemDecoderMP3`**; there are no system
AC3/EAC3/PCM or encoded-passthrough switches. **Dolby Atmos (E-AC3 JOC) stays on the
direct/system-player path for now**; do not move it to FFmpeg merely for audio. Ordinary
HEVC/H.264 + AC3/EAC3 remains native. When AV1 requires the FFmpeg route, FFmpeg decodes
E-AC3 itself to PCM and **loses Atmos**. A later goal is to patch FFmpegInteropX so AC3/E-AC3
can use the system audio decoder or passthrough, rather than lose the object metadata.

FFmpegInteropX moves its preferred audio
stream first; Bale restores Silo's audio ordinal by sorting `AudioStreams[].StreamIndex` and sets
`MediaPlaybackItem.AudioTracks.SelectedIndex` after MediaOpened, **before Play**. A missing or
unmappable planned track fails rather than silently playing FFmpeg's default. A server progressive
remux has only its selected audio stream, so its local ordinal is zero.

**Debug override:** `bale-debug.sh ffmpeg policy` is the default production rule.
`bale-debug.sh ffmpeg off` never uses FFmpeg and withdraws AV1 software/audio fallback claims
before planning (including the software-video opt-in). Only `policy` and `off` are accepted; the
memory-only setting is captured at attempt creation so toggling during a request cannot mismatch
claims and engine. Backend, actual per-stream decoder engines, codecs and CPU cores remain in
Stats / `where`. The CLI's old `--claim-av1` is a compatibility no-op; its normal baseline now
contains only the measured, bounded AV1 claim. FFmpeg packages, activation and licenses ship
with or without `BaleDebugServer`; the CI package checker remains required.

**Debug FFmpeg input A/B experiment (`DEBUG_SERVER` only):**

```sh
scripts/bale-debug.sh ffmpeg-tune
scripts/bale-debug.sh ffmpeg-tune [readahead=on|off|default] [readsize=N|default] [prefetch=N|default] \
  [buffer=ms|default] [ahead=N|default] [sws=N|default] [delay=N|default] [prio=N|default] \
  [maxres=0|1|default] [dpb=N|default] [grain=on|off|default] [upload=on|off|default]
```

No arguments reads the current process-wide overrides (`GET /ffmpeg-tune`); arguments update them
atomically (`POST /ffmpeg-tune` with the same query keys). Keys may be supplied in any order, once
each. Omitted keys keep their previous overrides; `default` clears only that key, restoring its
release setting. Restarting the process clears all overrides. Settings are captured at playback-attempt
creation, retained through recovery/remounts, and applied only to the next real FFmpeg Direct player
attempt, not an already open player, Native Direct, HLS or the standalone probe. `ffmpeg policy|off`
remains independent.

- `grain=off` (software video only) sends `bale_dav1d_export_film_grain=1` like the probe's `grain=off`:
  dav1d exports AV1 film-grain parameters instead of applying them, so frames show no grain. It exists
  to time grain cost on authenticated library files, which the URL-only probe cannot open. `on`/`default`
  send no key (release behaviour, grain applied).
- `upload=on` (software video only, wrapper `2.1.0-bale.6`+) sends `bale_d3d11_upload=1`;
  `off`/`default` send no key and use CPU buffers. Status always reports `upload=on|off`.
  This switch is independent of `grain`; the standalone probe and production never set it.
- `readahead=on` enables FFmpegInteropX's native demux/packet read-ahead queue with **64 MiB /
  30 s** caps, rather than its larger 100 MB / 60 s defaults, to bound the extra Xbox memory use.
  These are the wrapper's queue thresholds, not a total-process memory ceiling. `off` explicitly
  disables it; `default` does not set any read-ahead property (native default: disabled).
- `readsize=N` sets `General.FileStreamReadSize`, in **bytes**, inclusively 16384–1048576.
  `default` leaves the native 16384-byte default alone.
- `prefetch=N` sets the authenticated `HttpRangeStream` depth, inclusively **2–16** aligned 1 MiB
  blocks. The default stays **2**. This immutable per-stream Core hook does not change the 32-block
  cache or the three-concurrent-fetch limit; deeper speculation queues behind that limit.
- `buffer=ms` sets `MediaStreamSource.BufferTime`, inclusively **0–5000 ms**, with explicit overrides
  (including zero) winning on any FFmpeg route; `default` restores **2000 ms for software video** and
  leaves system-video routes untouched (wrapper default: zero). This is the release setting chosen
  from the console A/B results above; it can retain substantial decoded-frame memory and is independent
  of demux read-ahead.
- `ahead=N` writes decimal-string `FFmpegOptions["bale_decode_ahead_frames"]`, inclusively **0–64**
  decoded frames; **0 = off**. This is native decoded-frame lookahead, not the packet read-ahead queue.
- `sws=N` writes `FFmpegOptions["bale_sws_threads"]`, inclusively **1–16** scaler threads;
  **1 = upstream behavior**.
- `delay=N` writes `FFmpegOptions["bale_dav1d_max_frame_delay"]`, inclusively **0–64** frames;
  **0 = dav1d default**. It applies only to the libdav1d decoder.
- `prio=N` writes `FFmpegOptions["bale_decode_thread_priority"]`, inclusively **0–2**:
  **0 = off**, **1 = THREAD_PRIORITY_ABOVE_NORMAL**, **2 = THREAD_PRIORITY_HIGHEST**. It boosts
  the decode-ahead worker and newly created libdav1d / threaded swscale workers. The release default
  is **unset** (no key emitted, no boost); this is a debug-only experiment, not production tuning.
- The fork consumes these four options itself, never forwarding them to FFmpeg. Numeric values use
  strict invariant decimal parsing (no signs, spaces, fractions or exponents); invalid native values
  return `E_INVALIDARG`. On software video, explicit overrides win over release defaults, including
  `ahead=0` / `delay=0` / `prio=0`. `default` removes only that override, restoring ahead=16, sws=4,
  the size-dependent delay setting or an unset priority key. Description text appends
  `ahead=N sws=N delay=N prio=N` only for explicit overrides. Hardware/system video ignores these
  four overrides; the input/read-ahead options remain available on all real FFmpeg routes.
- Production (`BaleDebugServer=false`) applies the release software-video defaults, never the
  memory-only debug overrides. The A/B facility does not change the production decoder allowlist
  or establish a verified performance improvement.

For an isolated comparison, close the current player, set the baseline, then open the same title:

```sh
scripts/bale-debug.sh ffmpeg-tune readahead=default readsize=default prefetch=default \
  buffer=default ahead=default sws=default delay=default prio=default
# After closing the baseline player, change only native read-ahead and reopen:
scripts/bale-debug.sh ffmpeg-tune readahead=on
# Optional, separate experiments (close/reopen after each change):
scripts/bale-debug.sh ffmpeg-tune readsize=65536
scripts/bale-debug.sh ffmpeg-tune prefetch=8
scripts/bale-debug.sh ffmpeg-tune ahead=8
scripts/bale-debug.sh ffmpeg-tune sws=2
scripts/bale-debug.sh ffmpeg-tune delay=1
scripts/bale-debug.sh ffmpeg-tune prio=1
```

The player logs the actual native read-ahead enable/size/duration, read size and stream prefetch
once per attempt, without media request details. The read-only stats panel (`I` / View) and `where`
share **Read-ahead**, **Stream reads** (bytes / prefetch blocks) and **Stream waits** rows. Stream
waits count reads that find an unfinished demand or speculative block fetch and await it; they
exclude ready-cache hits, empty/EOF reads, the serialized-read lock and speculative work that no
read waits for. Total/max wall-clock milliseconds cover completed waits, including failed/canceled
reads and any fetch retries, counted once per read. Counts include an in-progress wait before its
duration is added, include opening/seeking (unlike late-frame counts), and reset with each mounted
stream. Non-range HTTP, HLS and absent streams show waits as unavailable, not measured zero.
The effective FFmpeg rows show unavailable on non-FFmpeg routes. Compare wait bursts with late
delivery; neither counter is evidence of renderer drops or decoder CPU saturation.

- Debug-only `bale-debug.sh frame-trace` prints totals and the last 300 software-video late deliveries,
  with previous-sample gap, delivered-ahead, stream-wait and GC deltas. Classes, in precedence order:
  `stream` (wait time increased), `gc` (pause delta ≥4 ms), `gap` (delivery gap >2× sample duration),
  `other`. These are correlations, not proven causes or renderer drops. `frame-trace reset` clears
  the trace/totals; starting and seeking also reset it, and seek samples are skipped.
- All builds hold `SustainedLowLatency` GC mode from playback start until page shutdown (including
  suspend/navigation); overlapping pages share leases, and the last release restores the pre-playback mode.
  Debug-only `bale-debug.sh gc` reports mode, `playback-lease=0|1`, pauses and the last collections,
  plus process-lifetime managed allocations (`allocated=... MiB`, approximate; compare deltas over equal intervals).
  `gc latency batch|interactive|sustained|default` changes the app-wide mode immediately, in memory only;
  `default` restores the helper's saved pre-playback mode even if the first `/gc` request is during playback.
- Debug-only `bale-debug.sh frame-delivery [native|managed|off]` (`GET /frame-delivery`,
  `POST /frame-delivery?mode=native|managed|off`) selects the regular player's software-video observer for
  the next playback attempt; no argument prints `frame-delivery=native|managed|off`. Native is the restart
  default (`on` is an alias): Rust observes/releases samples without managed wrappers or forced GC;
  a background C# thread samples GC/stream counters every 25 ms and drains ordered records every 250 ms
  into the existing delivery meter/trace. Counter lookup allows a 50 ms post-delivery sampling race;
  classifications remain concurrent evidence, not proven causes. The bounded native ring drops oldest
  on overflow; diagnostics then fail closed rather than silently lose seek markers or samples.
  Managed retains the old per-sample WinRT observer and forced gen-0 collections for A/B comparison.
  Off leaves late-frame, delivered-ahead and frame-trace diagnostics unavailable. The attempt logs
  `player: frame delivery observer=native|managed|off`; reopen the same title to change backends.
  The standalone FFmpeg probe remains managed. Console performance remains to be verified.
- FFmpeg routes use a pooled, read-only random-access stream adapter in all builds. It rents temporary
  managed storage and copies into FFmpeg's own native `IBuffer`, returning the same buffer instead of
  allocating a pinned CsWinRT replacement for each read. Native direct and HLS routes are unchanged.
  Debug-only `bale-debug.sh stream-adapter [pooled|builtin]` (`GET /stream-adapter`,
  `POST /stream-adapter?mode=pooled|builtin`) selects the next playback attempt's FFmpeg adapter;
  no argument prints `stream-adapter=pooled|builtin`. It defaults to pooled and resets on restart.
  The attempt captures the switch and logs `player: stream adapter=pooled|builtin` once, including
  across seeks/remounts. Close/reopen the same title for A/B allocation and GC-pause deltas; an open
  player and the standalone probe are unchanged. Console performance remains to be verified.

The standalone `playtest-ffmpeg` URI probe is for public or independently fetchable media. A private
`play-plan --url-file` file protects the URL on disk, but does not convey Bale's profile/authentication
headers to FFmpeg. Use the real-player toggle for library files, under **Bale Test**.

Standalone syntax (independent of process-wide `ffmpeg-tune`):

```sh
scripts/bale-debug.sh playtest-ffmpeg <url|@file|suite> [seconds] [auto|software|system] \
  [threads=N] [10bit=on|off] [rate=R] [ahead=N] [sws=N] [delay=N] [prio=N] \
  [maxres=0|1|default] [dpb=N|default] [hdr=on|off|default] [grain=on|off|default]
```

Supply seconds/mode before tuning, then keys in any order, at most once each. Seconds are 1–60
(default 3), threads are 0–16 (0 = logical CPU count), 10bit is on/off, and rate is an invariant
0.5–2.0 (default 1). The fork keys have the same ranges as above; software-mode probes use the release
ahead=16 / sws=4 defaults when omitted. URI probes have no playback plan/dimensions before creation,
so delay remains unset unless explicitly overridden (use `delay=8` for a known above-1080p comparison).
Priority remains unset unless `prio=0..2` is supplied, with the same levels as above. These four
fork options affect only software video, including an auto-mode probe that selects software decoding;
hardware/system video ignores them. Auto/system modes retain wrapper defaults for omitted keys.
`hdr=on|off` forces the wrapper's `Video.HdrSupport` to Enabled/Disabled (omitted or `default` keeps
Automatic, which applies HDR colour info only when the HDMI mode is already BT.2020); the report adds
`HDR support:` and the video stream's `HasHdrMetadata` / `IsHdrActive`. Production never sets it.
`grain=off` sends `bale_dav1d_export_film_grain=1` (wrapper patch 0005, `2.1.0-bale.5`+; older wrappers
reject the key and the open fails): libdav1d skips film-grain synthesis and exports the parameters as
frame side data, so a grain-carrying AV1 clip can be timed with and without grain (`Film grain:` in the
report). `on`/`default`/omitted keep normal synthesis and send no key. Production never sets it.
Console check on 0.1.291.0 (bale.5, 20 s `software`): the key is accepted and reported as exported.
The public Jellyfin AV1 clips carry no grain (`film_grain_params_present = 0` in the 1080p and 4K
sequence headers), so the 1080p 10 Mbps run is unchanged (ratio 0.992 / 1.87 cores with synthesis,
0.998 / 1.90 exported).

**GPU grain, stage 1 (wrapper patch 0006, `2.1.0-bale.6`).** Debug-only
`ffmpeg-tune upload=on|off` selects D3D11 texture delivery for software NV12/P010 output on the
next real-player attempt (memory-only, default off; `default` also restores off). The existing
copy/sws output step writes directly into a three-texture STAGING ring, then CopyResource + Flush
uploads to pooled DEFAULT textures on the MSS renderer's multithread-protected device. Samples use
`CreateFromDirect3D11Surface`; Processed returns textures, and seek/size changes discard the old pool
without reusing MF-held surfaces. A device/texture/map/surface failure permanently falls back to CPU
buffers for the stream, including the failing frame. No grain shader yet: leave `grain=default` for
an upload-only CPU/visual A/B; `grain=off` still removes grain, independently. Playback and CPU parity
need console verification; CI only builds/checks this path. Wrapper diagnostics use its existing
native logger, which Bale deliberately keeps disabled (no new native log capture).

**Grain cost, measured (0.1.293.0, Series X, app mode).** A 1080p23.976 AV1 10-bit SDR library movie
(mkv, 6.6 Mbps average, Opus 5.1) whose sequence header has `film_grain_params_present = 1` and
`apply_grain = 1` on every frame, played in the real player (FFmpeg Direct, software AV1) as Bale Test
with `ffmpeg-tune grain=default|off`, alternating runs, CPU sampled every 2 s. Scenes were chosen from
a bitrate map built from the file's Matroska Cues (`bale-cli play-plan --save-range`): median 6.2 Mbps
per minute, heaviest minute 18.6 Mbps, peak 10 s window 75 Mbps.

| Scene (bitrate) | Grain applied | Grain exported | Difference |
|---|---|---|---|
| Opening 0:15–0:45 (12.1 Mbps), 3 pairs | 1.53 cores | 1.16 cores | −0.37 |
| Heaviest minute (18.6 Mbps), 3 pairs, 60 s each | 1.81 cores | 1.56 cores | −0.25 |
| 75 Mbps 10 s burst, 1 pair, 24 s | 1.56 cores | 1.28 cores | −0.28 |

Grain synthesis costs about **0.25–0.4 cores at 1080p** (roughly 15–25 % of decode CPU), and every run
played in real time either way. Late frames in the heaviest minute were the same with and without grain
(13–23 per 60 s), so grain does not cause the 1080p late-frame bursts. Synthesis cost scales with pixel
count, so at 4K it would be about 1–1.6 cores, worth moving to a GPU shader only once 4K AV1 decode
itself keeps up (it does not today; see the MediaEngine section).
The system-video hints below accept `default` as omitted; other one-shot probe keys do not. The response
reports threads/10-bit output, logical CPUs, requested/applied rate, applied fork-option values with
default/override labels, and decoded output subtype plus software frame-delivery
measurements when available. Ratio/verdict use wall time multiplied by applied rate. Probe and override
code is absent with `BaleDebugServer=false`; release software-video defaults remain.

Results (0.1.229–0.1.230, Series X, Bale Test):

| Test | Result |
|---|---|
| Native AOT | FFmpegInteropX activates and plays; no AOT warnings. |
| Headless probe without a render target | FFmpeg-decoded video stalls at 0 s; it needs a surface (composition or MediaPlayerElement). |
| Chromium AV1 clips (8 and 10-bit), `auto` | dav1d software decode, real time. H.264/HEVC/VP9 go to FFmpeg's D3D11 decoder. |
| Fight Club, AV1 Main 10-bit 1080p + EAC3 5.1 MKV, `software` | **Direct play**, real time (30.1 s per 30 s), about **1.3 CPU cores**, picture correct. |
| Avatar: Fire and Ash, HEVC Main10 1080p + EAC3 7.1 MKV, `auto` | Direct play, real time, FFmpeg D3D11 video, 0.1–0.2 cores. |

These results used the earlier debug-only forced modes; they are evidence, not production route
choices. The standalone `playtest-ffmpeg` probe may still use `auto|software|system` for experiments;
those modes are not available to the real player.

The server only accepts a software `video_decode` entry when the client sends
`software_video_decode_v1` (silo-server `capabilities_v3.go` `videoEligibleV3`); Bale sends it
when the bounded AV1 entry exists. Both FFmpeg Create/open failures and MediaFailed use the
existing **single** failure-recovery replan. The request withdraws the triggering AV1 codec/decode
entry and software-video opt-in, and/or the failed selected fallback audio codec, from global and
per-delivery claims. It classifies the failure as `decoder_failure` with an explicit
`FFmpegInteropX failed; server transcode required` reason and only a safe HRESULT diagnostic.
The server then transcodes video and/or converts audio instead of retrying the failing local decoder.
The source clock, selected tracks and attempt identities are preserved; a failed recovery still
shows the normal error notice/Retry. Core tests cover policy, bounded claims, track mapping,
withdrawal and serialization. The production integration still needs the owner's console checks.

### Native FFmpeg / dav1d build

Bale uses `FFmpegInteropX.UWP.Lib` **2.1.0-bale.5** with the custom x64 native package
`FFmpegInteropX.UWP.FFmpeg` **8.1.2-bale.dav1d.1.5.4**. The public 8.1.2 x64 `avcodec-62.dll`
embeds dav1d **1.0.0-0-g99172b1** (March 2022); upgrading the wrapper cannot update that static
library. This rebuild is an A/B candidate for occasional AV1 stutter/2–4-core CPU spikes, **not a
confirmed stutter fix**. The reported stutter has no supplied title or timestamp; the earlier
Fight Club validation above must not be mistaken for that scene. HUD CPU measures the whole
process, not dav1d alone. `Video.MaxDecoderThreads` remains upstream's default zero (available
logical CPUs); this change does not tune threads, broaden the AV1-only allowlist or raise bounds.
Console performance verification remains the owner's task.

[Native build 42001326165](https://github.com/Zorro92/bale-for-silo/actions/runs/42001326165)
published the release below. Its actual x64 `avcodec-62.dll` embeds **1.5.4-0-g54706fc6**;
`scripts/ffmpeg-native.json` records the independently verified package SHA256 and NuGet content
hash (obtained with NuGet's hash API, not inferred from a signed package's archive SHA512).

**Provenance:** the immutable baseline is upstream `v2.1.0`, commit
`88428c66e106852338b48554a4a00c9e48da588f`. Its FFmpeg gitlink is
`38b88335f99e76ed89ff3c93f877fdefce736c13`, matching the public native 8.1.2 nuspec. All its
submodule pins also match the wrapper package's recorded source commit
`841fc8e79346a235906f1dbd45be53034b2e2f52`; the two later release-tag changes fix parallel wrapper
builds and clean the FFmpeg intermediate directory, not dependency pins/configure flags. The
native nuspec records FFmpeg's commit, **not the outer build-repository commit**, so this establishes
the release source baseline, not the original packaging machine or a byte-identical rebuild.

Only `Libs/dav1d` moves: `99172b11470776177939c3d2bc366fe8d904eab7` → tag **1.5.4**, commit
`54706fc6bc0cdecab7e9593974a4039cc038fca7`. The workflow checks every other submodule against the
selected ref and leaves upstream `Build-FFmpeg.ps1`, dependency build scripts, compiler/SDK defaults
and `FFmpegConfig.sh` unchanged. Explicit defaults are UWP/x64/Release/shared, GPL/version3 off,
encoders/devices/programs off. **No x264/x265 or GPL/nonfree components are built or linked.**
Transport uses official GitHub mirrors for FFmpeg/dav1d/x264 without changing their pins. Packaging
alone removes x86/ARM64 globs from a copy of the upstream nuspec and adds `SOURCES.txt`; the wrapper
and its native activation registration are unchanged.

**Rebuild / integrate:**

1. Run `gh workflow run ffmpeg-native.yml --ref main` (and watch its run with
   `gh run watch <run-id> --exit-status`). Optional `-f upstream_repository=owner/repository`,
   `-f upstream_ref=<ref>` and `-f dav1d_tag=1.5.4` inputs permit a later Bale fork without editing
   the workflow. Defaults are upstream plus the immutable release commit above and dav1d 1.5.4.
2. The workflow uses the upstream build entrypoint, asserts disabled GPL/version3/nonfree/x264/x265
   in generated configuration and checks the built DLLs. Its release includes the x64 NuGet,
   `.sha256`, exact `SOURCES.txt`, configure log and corresponding `ffmpeg-native-sources.tar.gz`.
   The source archive retains all built dependencies but excludes downloaded tools/build outputs,
   Git credentials and unused GPL encoder sources.
3. Release `native-ffmpeg-8.1.2-dav1d-1.5.4` and its assets are **immutable**. A rebuild retains
   Actions artifacts but refuses to replace an existing release. A genuinely new published build
   needs a new package version/release tag before its reviewed hash can replace the pin.
4. `scripts/ffmpeg-native.json` pins the asset's SHA256. Windows CI downloads it into an isolated
   staging directory, verifies the hash, then populates `.cache/ffmpeg-native` before restore.
   The same script verifies/restores the wrapper fork using its separate pin (below). `NuGet.Config`
   maps both fork IDs only to that feed; the project requires exact versions. The downloader clears
   both cached extractions so restore uses the verified assets, including after a broad CI cache hit.
   For a local Windows build, run `pwsh -File scripts/restore-ffmpeg-native.ps1` first.
5. Update the project reference and both native entries in `src/Bale.Xbox/packages.lock.json`
   together with a reviewed package/hash change. Keep the wrapper on its pinned `2.1.0-bale.N`
   fork without changing the upstream 2.1.0 ABI. Run Core tests and watch debug **and** production
   MSIX CI. The package checker
   reads the actual MSIX `avcodec-62.dll` and rejects anything without dav1d **1.5.4**; update its
   expected version/tests deliberately for a later dav1d upgrade. Licenses and provenance ship in
   both configurations.

### Native FFmpegInteropX wrapper fork

Bale now requires exactly `[2.1.0-bale.5]` of `FFmpegInteropX.UWP.Lib`, from immutable release
[`wrapper-ffmpeginteropx-2.1.0-bale.5`](https://github.com/Zorro92/bale-for-silo/releases/tag/wrapper-ffmpeginteropx-2.1.0-bale.5).
The independently verified nupkg SHA256 is
`519386d2ff1c3a0d65a9edfce30df187b2510c6088dcc04c643fb0ddf4432fbd`. The release tag, SHA256 and
NuGet content hash are pinned in `scripts/ffmpeginteropx-wrapper.json`; both framework/RID lock
entries agree. `restore-ffmpeg-native.ps1` downloads it into isolated staging, verifies before moving
it to `.cache/ffmpeg-native`, and clears its cached extraction. Source mapping allows the wrapper
only from that verified feed, never nuget.org. `Licenses/FFmpegInteropX/SOURCES.txt` ships in debug
and production MSIX files next to the license texts. The package checker compares the actual MSIX
`FFmpegInteropX.dll` with `runtimes/win-x64/native/FFmpegInteropX.dll` in the hash-pinned nupkg, and
checks that the shipped wrapper provenance is identical too. Activation/factory and dav1d/license
checks remain enabled in both configurations.

The manual `ffmpeginteropx-wrapper.yml` workflow builds only the **Release_UWP x64** native wrapper;
producing a new release does not automatically update Bale's app pin.

`scripts/ffmpeginteropx-wrapper.json` pins upstream commit
`88428c66e106852338b48554a4a00c9e48da588f` and the original nuget.org 2.1.0 package SHA256
`927f23e66688b6c86659121fd20c12c44507581b3b00de5437274741bb506881`. The source checkout omits
submodules. Native FFmpeg comes only from `restore-ffmpeg-native.ps1 -NativeOnly` and its hash pin
(the wrapper build must not require an already-published wrapper);
upstream's `8.1.2-pre01` FFmpeg packages are never restored. Only the vcxproj's three pinned
build-tool packages are restored from nuget.org. The native MSBuild settings follow upstream:
`Release_UWP`, `x64`, toolset `v145`, SDK `10.0.26100.0`, minimum `10.0.17763.0` and
`LibraryVersionNumber=2.1.0.0`. The workflow bypasses upstream's full build/restore entrypoint so
it neither rebuilds the managed projection nor downloads a different FFmpeg. Compiler flags and
IDL remain unchanged; the native FFmpeg package version is explicitly overridden.

The released fork includes `scripts/ffmpeginteropx-patches/0001-decode-ahead-threaded-sws.patch`:
opt-in decoded-frame lookahead, threaded swscale and libdav1d max-frame-delay. It also includes
`scripts/ffmpeginteropx-patches/0002-decode-thread-priority.patch`, adding optional priority boosts
for the decode-ahead worker, libdav1d workers and threaded swscale workers. The four decimal-string
`bale_*` keys are documented above; production sets the three `SoftwareDecodeTuning` defaults,
never the priority key. Every `*.patch` is applied with `git apply --index` in ordinal filename order;
any failure aborts. Only `Source/` `.cpp`/`.h`
changes are accepted, **never IDL**, project files or dependency changes. The build compares the
built winmd's public type-name set against the original; a difference aborts packaging. This is a
minimum metadata guard, not a complete member/ABI comparison. Patches must preserve that ABI.

`0003-decoder-max-resolution.patch` prepares opt-in system/passthrough-video hints
`bale_decoder_use_max_resolution` (0/1) and `bale_decoder_max_dpb_count` (0–16; 0 leaves attributes
unset), never applied to FFmpeg-decoded output. Debug-only `maxres=0|1|default` / `dpb=N|default`
controls in `ffmpeg-tune` and `playtest-ffmpeg` both default to unset, leaving production unchanged.
It first shipped in `2.1.0-bale.3`.
Microsoft's Xbox recommendation (`maxres=1 dpb=3`) was A/B'd on 0.1.269.0 (Series X, app mode) with
20 s `playtest-ffmpeg ... system` probes of the public Jellyfin HEVC 10-bit clips. 1080p 10 Mbps:
every run played at real time (ratio 0.994–0.998) either way, and the hints raised peak commit by
~450 MiB. 4K 60 Mbps: ratios were 0.735/0.785/0.79/0.733 unset and 0.781/0.996/0.994/0.78 with the
hints, which is within network/start-up noise; peak commit was again higher with them. No measured
benefit, so the keys stay unset (not a release default). Revisit only for mid-stream resolution
changes, which these clips do not exercise.

`0004-mf-video-color-attributes.patch` backports upstream PR #482 (fix for upstream issue #481)
unchanged; the resulting `CreateStreamDescriptor` is identical to upstream `master`. It changes only
the media type of **FFmpeg-decoded** video (`UncompressedVideoSampleProvider`, including the FFmpeg
D3D11 path that inherits it); compressed/passthrough streams decoded by the system decoder are
untouched, since that decoder reports colour from the bitstream itself. Changes: `MF_MT_YUV_MATRIX`
is now set from `color_space` (BT.709, BT.601, SMPTE 240M, FCC, YCgCo, SMPTE 2085, BT.2020 NCL
10/12-bit, BT.2020 CL/chroma-derived, ICtCp); before, it was never set, leaving Media Foundation to
pick a default (typically from resolution), which is wrong for BT.2020 software-decoded AV1.
`MF_MT_TRANSFER_FUNCTION` maps BT.709/SMPTE 170M to `MFVideoTransFunc_709` (was gamma 2.2) and adds
sRGB, BT.1361 ECG, SMPTE 428 and BT.2020 constant luminance. `MF_MT_VIDEO_PRIMARIES` adds EBU 3213,
SMPTE 428 (XYZ) and SMPTE 431/432 (DCI-P3) and no longer maps reserved values. The existing HDR gate
is kept (HDR primaries/transfer and mastering/CLL metadata still need `applyHdrColorInfo`); the matrix
is set unconditionally. Nominal range and per-sample chroma siting are unchanged. The patch is
always on (no `bale_*` key) and first shipped in `2.1.0-bale.4`. Console check on 0.1.278.0 (`playtest-ffmpeg ... software`,
`Output colour:` line): the Jellyfin 1080p AV1 10-bit SDR clip (tagged bt709 matrix/primaries, transfer
unknown) reports `matrix=1(BT709) transfer=unset primaries=2(BT709) range=2(limited)`; the 1080p AV1
HDR10 clip (bt2020nc, smpte2084, bt2020) reports `matrix=4(BT2020_10)` with transfer/primaries unset,
because Bale leaves `Video.HdrSupport` at Automatic and the wrapper only applies HDR colour info when
the HDMI mode is already BT.2020 at creation (`UwpCheckUseHdr`). On an HDR TV the player switches HDMI
before mounting, so PQ/BT.2020 should then be set; that path is unverified (no HDR display here).
On 0.1.280.0 the same HDR10 clip with `hdr=on` reports `matrix=4(BT2020_10) transfer=15(2084/PQ)
primaries=9(BT2020)` and `IsHdrActive=True`; `hdr=off` and the default report transfer/primaries unset
and `IsHdrActive=False`. So the PQ/BT.2020 tags depend only on HdrSupport resolving to on, not on 0004.

**Software AV1 tuning A/B (0.1.274.0, 20 s `playtest-ffmpeg ... software`, two interleaved reps).**
1080p AV1 10-bit 10 Mbps 60 fps plays at real time (0.997–0.998) in every configuration: release
defaults (ahead=16, sws=4, priority unset) 1.91–1.98 cores, `ahead=0` 1.81–1.84, `prio=1` 1.91–1.96,
`prio=2` 1.98–2.01. `sws=1` vs `sws=4`: 1.72/1.85 vs 1.91/1.97 cores. 4K AV1 10-bit 40 Mbps stays
decode-bound at 0.53–0.58 (5.8–6.8 cores) whatever the setting; `sws=4` vs `sws=1` was 0.55–0.59 vs
0.48–0.54 in a separate run. Thread priority shows no benefit and stays unset. Decode-ahead costs
~0.1 core on these steady clips, which cannot show its jitter-absorbing purpose; it stays at 16 pending
real-library checks. A burst of ~150–170 late frames appeared in about half the 1080p runs regardless
of setting (also with sws=1); its cause is not yet known.

**Fixed debug-probe leak.** Through 0.1.271.0 each `playtest-ffmpeg` probe left ~25–30 MiB (1080p) to
~100+ MiB (4K) of native memory behind because its `MediaPlayer` was disposed from a worker after the
scope ended. Disposing it on the UI thread right after detaching the source, as `PlayerPage.DisposeMount`
does, holds app memory flat (~110–118 MiB over six probes on 0.1.274.0, `mem gc`). Repeated
real-player sessions never grew monotonically (470–650 MiB over ten plays).

Packaging starts from the hash-verified original nupkg, preserving its **winmd, managed projection,
XML, targets, documentation and x86/ARM64 binaries byte-for-byte**. Only the x64 DLL is replaced.
The built x64 PDB is added (the original nupkg has none), along with `SOURCES.txt`; nuspec and
OPC core-properties versions/content types are updated. The original NuGet repository signature
is removed because modification invalidates it. The fork is unsigned and intended for **x64 only**;
retained other-architecture binaries are not fork builds.

For the next release, run `gh workflow run ffmpeginteropx-wrapper.yml --ref main -f version=2.1.0-bale.6`,
then `gh run watch <run-id> --exit-status` (choose a fresh number if that tag already exists). The release contains the
nupkg, `.sha256`, `SOURCES.txt`, `winmd-public-types.txt`, build log and corresponding patched
wrapper source archive. Provenance records the upstream/Bale commits, ordered patch names/hashes,
original/native package hashes, native FFmpeg version, tool packages, SDK/toolset and runner image.
Existing release **tags** are rejected before building and again before publishing; choose a new
`2.1.0-bale.N` version rather than overwriting. This is a pinned, repeatable build recipe, not a
claim of bit-identical compiler output across hosted runner revisions. Before changing the app pin,
independently verify the new package and obtain its NuGet content hash, following the native FFmpeg
process. Update the wrapper JSON, exact project reference and both lock entries together; never
replace assets or silently repin a published version.

`test-ffmpeginteropx-wrapper.ps1` exercises empty/ordered/rejected patches, the IDL restriction,
WinMD type mismatch, SHA256 failure and byte-preserving repack with small offline fixtures. Both
Windows CI configurations and the manual wrapper workflow run it alongside the native checks.

### IMFMediaEngine spike and the Rust GPU layer (debug only)

`scripts/bale-debug.sh playtest-engine <url|@file|suite> [seconds] [cs|rust|rust-ffmpeg]` plays a clip
muted in IMFMediaEngine **frame-server mode**: `OnVideoStreamTick` + `TransferVideoFrame` into our own
BGRA texture on our own hardware D3D11 device (Xbox-ATG-Samples `UWPSamples/Graphics/VideoTextureUWP`,
`MediaEnginePlayer.cpp`), then reports the engine's own statistics (rendered/dropped/corrupted frames,
jitter, frame delay). `cs` is the C# probe (`DebugMediaEngine.cs`, raw vtable slots); `rust` is its typed
windows-rs port in `native/bale-gpu` (`bale_gpu_engine_probe`); `rust-ffmpeg` creates an FFmpegInteropX
source in software-decoder mode and hands it to the Rust engine (`bale_gpu_engine_probe_source`).
`scripts/bale-debug.sh rust` is the DLL loader check. Production playback is unchanged.

The bridge: the FFmpegInteropX MediaStreamSource does not QI to `IMFMediaSource`, but its `IMFGetService`
returns one for `MF_MEDIASOURCE_SERVICE`. An `IMFMediaEngineExtension` (`MF_MEDIA_ENGINE_EXTENSION`)
returns that source for a private URL, as Chromium's MediaFoundationRenderer does
(`media/renderers/win/media_engine_extension.cc`); the engine asks for a byte stream (refused) and then
a media source. `CreateMediaPlaybackItem` must run first: it installs the source's sample handlers.

Results (Series X, app mode, 3 s Chromium clips unless noted):

- 0.1.283.0 C# probe, `bear-1280x720.mp4` (H.264): canplay 990 ms, 82/82 frames, 0 dropped,
  TransferVideoFrame mean 2.7 ms, ratio 0.962, 0.076 cores.
- 0.1.287.0 Rust port, same clip: 82/82, 0 dropped, ratio 0.970, identical statistics to the C# probe.
  The DLL loads with a static CRT; std threads, `#[implement]` COM callbacks and D3D11 all work.
- Rust suite: H.264, HEVC (both fragmented clips) and VP9 play through system decoders; AV1 (mp4, webm,
  10-bit) and Opus fail at open with `0xC00D5212` (MF_E_TOPO_CODEC_NOT_FOUND: no AV1 or Opus MFT);
  AC3, EAC3 and FLAC audio-only clips open.
- 0.1.289.0 `rust-ffmpeg`, `bear-av1.mp4`: 82/82 frames, 0 dropped, ratio 0.984. So AV1 reaches
  MediaEngine through dav1d.
- 20 s A/B against the current MediaPlayer path (`playtest-ffmpeg ... software`), Jellyfin AV1 10-bit:
  1080p 10 Mbps 60 fps: MediaPlayer 0.995 / 1.89 cores, MediaEngine 0.989 / 2.31 cores (1187 frames,
  0 dropped). 4K 40 Mbps: MediaPlayer 0.521 / 5.87 cores, MediaEngine 0.528 / 6.79 cores (24 dropped).

Conclusion: MediaEngine adds no throughput. 4K AV1 stays dav1d CPU-bound on either renderer, and
frame-server copies cost ~0.4–0.9 core more. Its value is control (our own D3D11 device, per-frame access
for GPU passes, engine drop statistics), not speed. Only GPU-side AV1 decode (no D3D11 AV1 profile on
Xbox; a D3D12 compute decoder is the remaining option) or a cheaper decode (grain export via patch 0005,
`grain=off`) can move 4K AV1.

## Display HDR

Display HDR is detected at runtime on the UI thread at each player start, not inferred from the
developer's SDR display or installed codecs. `DisplayHdrOutput` reads the supported HDMI modes;
`DisplayHdrPolicy` claims HDR10 if any non-stereo mode supports SMPTE ST 2084 (PQ). ST 2086
metadata support is recorded but is not required for that claim. A successful read reports
`output.display.hdr_evidence = "exact"`, including an empty claim for a non-HDR display. A failed
or unavailable read reports `"unknown"` with empty HDR, so the server fails closed. Both production
and the debug FFmpeg `policy|off` paths use the detected display. `caps` shows the claim/evidence.
HDR10+, HLG and Dolby Vision remain unclaimed/unverified; Xbox Dolby Vision is low-latency DV
(profile 5) only, to revisit later.

**HDR sources on a non-HDR display go to the server for transcode and tone mapping, never console
tone mapping.** The server's `outputRangeEligibleV3` / `nativeOutputHDRV3` gates direct HDR on
exact display evidence and supported HDR types. Microsoft documents automatic media-pipeline
HDR-to-SDR tone mapping, but Bale deliberately does not rely on it: the product policy remains
server tone mapping. The bare `ConsoleEvidence.Default` is unknown until augmented by display
detection; CLI planning without HDMI evidence stays conservative.

**Debug-only tone-mapping comparison:** `scripts/bale-debug.sh hdr pretend|off|status` defaults to
`off` and is memory-only (restart resets it). `pretend` affects later playback attempts: it claims
only HDR10 with exact display evidence, preserves the FFmpeg policy and all other capabilities,
and skips the HDMI switch entirely. The current SDR output is left alone so Microsoft's media
pipeline can tone-map a direct HDR10 picture (including the server's validated HDR10 base-layer
fallback for HDR10+). This is an explicit experiment, not a production-policy change or proof of
picture quality. Stats and `where` show `HDR output: console tone map (debug)` for that plan;
turning the toggle off mid-play does not change the captured attempt. Restart playback after
`hdr off` to compare with the normal server tone-mapped transcode.

For the same-frame comparison, `scripts/bale-debug.sh seek <seconds> [pause]` seeks the active
native, FFmpeg or HLS player to absolute **source/movie** time, optionally pauses, waits for seek
completion and prints actual source/player positions. HLS timeline offsets are mapped rather
than treating a reanchored player clock as movie time. Non-seekable/loading players, positions
outside the current timeline/duration and seeks that do not complete within 10 seconds fail
explicitly. These endpoints and the player hooks compile only under `DEBUG_SERVER`; production
builds cannot use either override.

**Decision: keep server tone mapping.** The owner's console comparison on **0.1.237** used
*Avatar Aang* (1080p HEVC Main10 HDR10+) at **600 / 1800 / 3000 / 4500 seconds**. The media
pipeline's automatic HDR-to-SDR conversion (`hdr pretend`) looked washed out and desaturated,
with lifted blacks; the server's BT.2390/Hable transcode retained color and contrast. Keep
`hdr pretend` as a debug comparison tool, not the normal SDR playback path.

Microsoft's media pipeline exposes no tone-mapping algorithm or parameter controls.
FFmpegInteropX's `SetFFmpegVideoFilters` is not a practical alternative: filters run on the CPU,
this build has no zimg/zscale or libplacebo, and Xbox offers no usable OpenCL/Vulkan path for
`tonemap_opencl` or libplacebo. The configure log's `vulkan=yes` is not Xbox runtime evidence.
HEVC on the FFmpeg route is hardware-decoded by the system, so its decoded frames never reach
FFmpeg's filters. A Bale GPU tone-mapping shader remains a possible future option
**only if server transcoding becomes a bottleneck**.

Before an HDR10 direct-play mount (including progressive remux with an unchanged HDR picture),
Bale requests a non-stereo PQ HDMI mode with `HdmiDisplayHdrOption.Eotf2084`. It keeps the current
resolution/refresh when available, otherwise chooses the smallest supported resolution at least
as large, then the closest refresh rate. Media modes are capped at 60 Hz: Microsoft's Xbox
guidance explicitly excludes 120 Hz for media playback, even when that is the desktop mode. Server-validated HDR10 base-layer playback of HDR10+ /
legacy unknown-range sources switches the same way. The default production behavior still preserves
resolution/refresh; the debug-only content-matching experiment in section 6 combines the PQ and
refresh selection in one request. HDR10 direct play switches HDMI to HDR and restores the default afterwards: Back,
end of playback, error, replan to transcode, page unload/navigation and app suspend all restore it.
Restore failures log only a boolean and retain restore authority for the next teardown attempt.

A false/throwing switch request, no eligible mode or lost display uses the existing **single**
failure-recovery replan before opening media. The request withdraws all global/output/per-delivery
HDR claims, sets display evidence to unknown, and asks for server tone mapping at the same source
position with the same selected tracks and attempt identity. This is an output failure, not a codec
failure: working decoder claims stay intact. A replacement that still requires HDR, or a failed
recovery, shows the normal error/Retry rather than knowingly sending HDR as SDR.

The manifest declares the restricted `hevcPlayback` capability required for Xbox HDR10/4K.
This opts into the 3.25 GB media memory budget and prevents simultaneous app/game execution:
launching a game suspends/closes Bale, and launching Bale waits for a game to close. It does not
broaden the measured codec/resolution claims above or add a background-music feature.

References supplied by the owner:
- [Microsoft: 4K video playback for UWP apps on Xbox](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/hevc-xbox)
  documents the manifest flag, HDMI switch/restore APIs and media refresh-rate restrictions.
- [Microsoft: supported media type strings](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/supported-play-type-strings)
  describes `ProtectionCapabilities.IsTypeSupported` decode/display queries as a future source of
  bounded evidence; it is not proof of actual playback. Audio `codecs=ec-3` decode support is
  distinct from an endpoint's `audio-endpoint-codec=DD+JOC` support. The endpoint feature is
  Media Foundation-only, not supported by WinRT query APIs. No new passthrough claim is made.

**Untested on real HDR hardware so far.** Pure mapping, selection, serialization and HDR-claim
withdrawal have Core tests, plus output failure/restore tests using an HDMI stand-in; Windows CI
builds both debug and production Native AOT packages.

## Phases

Each phase ships on its own, is verified on the console and is ticked here.

### 1. Playback session (Core, testable on Linux)
- Add the `/api/v2/playback/*` paths to `scripts/generate-api.sh`: `capabilities`, `start`,
  `{id}/progress`, `{id}/stop`, `{id}/replan`.
- `PlaybackStartRequestV3`, following the official model:
  - file id, profile id, attempt id, start position
  - audio/subtitle track from the Detail selectors
  - `client_capabilities` and `client_playback_context.deliveries`: original_http / progressive /
    hls with containers, video codecs, `audio_decode_codecs`, `audio_passthrough_codecs`,
    max channels, HDR details
- A capability builder fed by the probe results, replacing the static `XboxAudioCapabilities`
  baseline.
- Progress, the official way:
  - a sequenced writer: increasing `sequence`, `installation_id`, `stop_id`
  - positions recorded every 10 s (`POSITION_RECORD_INTERVAL_SEC`)
  - a final position on stop
  - a small durable journal, so a crash still reports the last position next launch
    (`SequencedPlayback`'s `PlaybackJournalEntry`)
- Exercised against the server under the **Bale Test** profile only.

### 2. Bare player screen
- `PlayerPage`: full screen `MediaPlayerElement`, opened from Play / Resume on Detail.
- Loading spinner and error notice (`TvPlayerNoticeOverlay.kt`). The `MediaFailed` reason goes
  to `replan` for one server-side fallback before the error shows.
- Back stops playback (stop + final position) and returns to Detail with focus restored.
- Verify: plays direct and transcoded titles, resume point lands, Detail shows the new progress.

**Done, verified on the console (0.1.215, Bale Test profile):**
- **Transcoded HLS** (`?st=` stream token) plays, and so does **direct MKV** (`original_http`, 1080p EAC3). Direct play keeps real time: 15.04 s of source per 15 s.
- **Resume** lands 7 s early (`PlaybackStartPosition.kt`). Start Over starts at 0.
- **Back** stops playback and restores focus to Resume / Start Over. The Detail label refreshes, e.g. Resume 14:38 → 15:20.
- **Spinner** follows `PlayerSpinnerVisibility.kt`: 500 ms show delay, 500 ms minimum.
- **Playback failure:** a fault triggers one `replan`, then the error notice with Retry focused. Retry resumes at the failed position with no rewind (`TvPlayerViewModel.kt` `retry()`).

Things learned on the way:
- **Server:** it only stores a resume point at ≥ 5% of runtime.
- **Direct-play evidence:** the server needs `video_decode` entries before `platform_attested` evidence allows direct play (Apple's `AppleDecodeCapabilities.swift`).
- **Range reads:** Windows' MKV source alternates read positions, so `HttpRangeStream` serves 1 MiB blocks from a small LRU cache with read-ahead. With one request per jump, playback crawled.

### 3. Transport overlay (`TvPlayerScreen.kt`, `TvPlayerTransportCluster.kt`, `TvPlayerScrubber.kt`)
- Controls auto-hide after 5 s (`CONTROLS_AUTO_HIDE_MS`). Skip feedback shows 1.2 s.
- From clean playback:
  - Left/Right skip
  - Down opens the playback HUD
  - Play/Pause acts on the first press, consuming repeats
  - Menu opens the settings HUD on key-up (`TvPlayerRemoteKeyAction.kt`)
- With the overlay up, Left/Right move focus.
- Scrubber: focus enters scrub mode, Left/Right nudge the preview, Select commits. Chapter ticks
  and time bubble. Hold-seek (`TvHoldSeekIndicator.kt`, 300 ms threshold).
- Back: overlay first, then leave the player.

### 4. HUD (`TvPlayerHud.kt`)
- Tabs: Info · Stats · Video · Audio · Subtitles · Chapters (`visibleHudTabs`: Info and Video
  always, the others when they have data).
- Audio/subtitle switching mid-playback, as a local track switch or a `replan` when the server
  must re-plan.
- Chapters seek and close the HUD.

### 5. Up next and auto-play
- Next-episode card with a 10 s countdown (`NEXT_UP_COUNTDOWN_SECONDS`), auto-play-next setting.
- Watched state at the end, next episode becomes the series' Play target.

### 6. Display mode matching
- **Debug switch implemented; console HDMI verification pending.**
  `scripts/bale-debug.sh display-match [on|off|status]` (no argument = status) is memory-only,
  defaults to off and resets on restart. It is captured for the next playback attempt, never
  changes an open player, and is absent with `BaleDebugServer=false`. Production behavior is unchanged.
- Matches Android TV's `HdrDisplayController.kt` / `RefreshRateSelector.kt`: first exact video
  width/height, otherwise modes that hold the video, otherwise the largest modes. Integer-multiple
  refresh rates beat non-integer cadence; closest refresh wins within that class. Rates within
  0.005 Hz of the best delta tie, preferring 16:9 over DCI 4096×2160, then the largest pixel area.
  Xbox candidates exclude stereo and anything above 60 Hz, even on a 120 Hz desktop.
- At mount, use the plan's frame rate (`PlaybackStats.ResolveFrameRate`; recipe/source for HLS)
  and output dimensions (source for original delivery, otherwise effective recipe with source
  fallback). Unknown frame rate/size skips matching and logs why; normal HDR10 negotiation remains.
  Direct HDR10 restricts the same selection to PQ and makes one `Eotf2084` request; other matched
  content requests `HdmiDisplayHdrOption.None`. `hdr pretend` still bypasses all HDMI changes.
- Equal current modes and repeated width/height/milli-fps signatures avoid redundant handshakes
  (the signature also includes HDR10 output). Requests share HDR's existing default-mode restore
  authority on Back/end/error/replan/unload/suspend, including retry after a failed restore. A failed
  HDR match uses the existing HDR-withdrawal replan; SDR matching failure does not fail playback.
- The safe log is `display match: content=<fps> <W>x<H> picked=<W>x<H>@<Hz> switched=<bool>`.
  Debug Stats / `where` add **Display mode**, reading current HDMI timing and active HDR/SDR rather
  than treating PQ support as active HDR. **Later:** the Match Content Frame Rate Settings row and
  mid-playback video-size/frame-rate notifications, as in `TvPlayerScreen.kt`.

### Later
- Shuffle, intro/credits skip (`TvIntroAutoSkipBanner.kt`), trickplay thumbnails.
- Subtitle search / styling.
- Watch Party.
- Audiobooks.

## Testing rules
- Every server write (progress, watched, track picks) runs under the **Bale Test** profile.
- Drive the player with `bale-debug.sh key/press` and check it with `where`, `wait` and
  screenshots.
