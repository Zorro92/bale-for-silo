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
| WebM VP8+Vorbis, VP9, VP9+Opus | opens, decoders report FullySupported, but never leaves Paused at 0 s. Unexplained. This is Windows' own WebM source, so Bale never uses it: VP9 goes through the FFmpeg demuxer (see the claims below). |
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

D3D12 video decode (Bale 0.1.340.0, app mode, 2026-11-08): `ID3D12VideoDevice` is available on the
default device and reports 11 decode profiles. They are the same 11 profiles D3D11 lists (VC-1,
H.264 incl. multiview, MPEG-2, MPEG-4 Part 2, HEVC Main/Main10, VP9 profile 0/2). **No AV1 under
D3D12 either**, so fixed-function AV1 decode isn't hidden behind the newer API. Every per-size
`DECODE_SUPPORT` query reported no supported NV12/P010 size, even for HEVC, which D3D11 decodes up
to 8K. Bale 0.1.343.0 prints why: the first `DECODE_SUPPORT` query returns 0x887A0005
(`DXGI_ERROR_DEVICE_REMOVED`, SupportFlags 0), and every later query fails the same way. The video
query removes the D3D12 device, so D3D12 video decode is unusable in app mode, not merely missing AV1.
Keep that query out of anything that shares a device with playback. It doesn't change the AV1
conclusion, which rests on the profile list.

Still to test with real library files: TrueHD/DTS passthrough to a receiver.

The capability claims Bale sends the server must come from this kind of evidence, never from
guesses. For hardware video, the hardware decoder's own report is that evidence: at launch Bale
asks the D3D11 video device (`HardwareDecodeProbe`, the same walk `caps gpu` prints) for the
largest of 1080p, 4K and 8K each decoder profile accepts, and claims exactly that. Software
decode and audio still need measured playback. `playtest` (debug only) checks actual playback,
not just installed decoders.

**Decision: ship FFmpegInteropX as the production fallback.** By the project owner's rule,
software **video** decode is **AV1 only**. `SoftwareDecodePolicy.VideoCodecs` is an explicit,
owner-maintained allowlist; nothing else may be added without them. Unsupported non-AV1 video
goes to the server to transcode, never FFmpeg software or D3D11 video. The measured AV1 evidence is
8/10-bit, max 1920×1080, max 60 fps: 0.1.230 Fight Club (1080p23.976 10-bit, ~1.3 cores) and 0.1.439
SmackDown (1080p59.94 10-bit, 6.4 Mbps, zero-copy, 0 late frames over ~4.5 min, ~3 of 8 cores).
**4K software AV1 is not claimed in production.** The debug `av1-claim 2160` bound adds a separate
3840×2160 entry capped at 30 fps; the server's platform-attested rates are absolute and it accepts a
source that any one entry admits. Audio may use FFmpeg software whenever the system path cannot handle it.

Current production capability claims (`ConsoleEvidence.ForHardware` over the decoder probe, plus runtime display detection, max audio channels **8**):

| Stream | Claims / engine | Bounds / limitations |
|---|---|---|
| H.264 / HEVC / VP9 video | Media Foundation, hardware | The largest size the console's decoder reports per codec, 60 fps, no bitrate limit. H.264 is 8-bit; HEVC is 8-bit if Main is present and 10-bit if Main10 is; VP9 is 8-bit for profile 0 and 10-bit for profile 2. Series X: H.264 1920×1080, HEVC 7680×4320 8/10-bit, VP9 3840×2160 8/10-bit. VP9 is claimed only when the decoder reports it. If the probe fails or finds nothing: H.264 and HEVC at 1920×1080, 60 fps, 25 Mbps, no VP9 |
| VC-1 / MPEG-2 video | FFmpeg demuxer, Media Foundation system decoder | Only when the matching NV12 VLD profile reports a supported size; 8-bit, probed bounds. Series X: both report 1920×1080. Original files only; HLS and `ffmpeg off` withdraw these codecs. Software video decoding remains forbidden |
| AV1 video | dav1d, **non-hardware**; `software_video_decode_v1` opt-in | 8/10-bit, **1920×1080, 60 fps**; no 4K claim |
| System audio | `aac`, `ac3`, `eac3`, `mp3`, `pcm` | Retains the native direct path unless AV1 needs FFmpeg |
| FFmpeg fallback audio | `dts`, `dts_hd`, `truehd`, `flac`, `opus`, `vorbis`, `alac` | Decode to PCM, not passthrough; real-library console verification pending |
| Audio passthrough | None | Receiver verification pending |
| Display HDR | Runtime HDMI detection, HDR10 only | Exact evidence after a successful read; unknown/empty HDR on failure |
| HLS | System video/audio claims only, without VP9, VC-1 or MPEG-2 | AdaptiveMediaSource cannot use FFmpeg fallbacks or its demuxer |
| Subtitles | Own overlay: text sidecars (ASS/SSA, SubRip, WebVTT); PGS/DVD/DVB images from direct MKV; lossless PGS `.sup` windows on HLS/progressive | Original keeps `native_embedded` (`embedded_subtitles_v1`). Packaged deliveries claim `embedded_bitmap` for server-extracted PGS; DVD/DVB still require burn-in there. Console verification of image tracks pending |

`PlaybackRoutePolicy.Decide` selects the pipeline separately for every mount. `original_http` /
progressive uses FFmpeg for Matroska, AV1, VP9/VC-1/MPEG-2, or the **selected** fallback audio codec. AV1 sets
`Video.VideoDecoderMode = ForceFFmpegSoftwareDecoder`; the other FFmpeg routes set
`ForceSystemDecoder`. Never use automatic or FFmpeg D3D11 video in real playback. The pinned
2.1.0 implementation can fall back even in ForceSystemDecoder for unsupported formats, so Bale
also validates all reported video decoder engines before mounting/reading frames.

**VC-1 / MPEG-2 verification (2026-11-11).** The Series X reports NV12 1080p for
`D3D11_DECODER_PROFILE_VC1_VLD` and `D3D11_DECODER_PROFILE_MPEG2_VLD`. Before enabling
the normal-player claims, three original MKVs were tested through FFmpegInteropX's forced system
decoder for 30 seconds each: A Charlie Brown Christmas (1080p23.976 VC-1 Advanced),
It's Christmastime Again, Charlie Brown (1080p23.976 VC-1 Advanced), and Bratz: Genie Magic
(720×480 MPEG-2 Main, catalog frame rate 30.303). Every probe reported `DecoderEngine=system`,
Playing, no buffering or decoder failures, and a media/wall-clock ratio of 0.997–0.998.
These probes validate the compressed WVC1/MPEG2 providers; they do not validate Windows' own
container sources or HLS. The installed build previously transcoded all three as unsupported
video.

On **0.1.462**, full-player playback of A Charlie Brown Christmas is `original_http`, VC-1 system
video and MP3 system audio; forward/back skips and a five-minute seek retain direct play. Bratz is
also `original_http`, MPEG-2 system video and AC3 system audio. Its English DVD subtitles reach
Bale's bitmap overlay at source stream 2, with a visible cue confirmed on the console and no
burn-in. Seeks to 240/300 seconds and back/forward skips retain this route; a paused seek to 60
seconds clears the old cue and leaves no stale image. The standalone probe's software MP3/AC3
audio choices therefore do not describe the production player, which enables the system switches.
It's Christmastime Again, Charlie Brown also direct plays its VC-1 through the system decoder,
with local FFmpeg FLAC audio decode. Forward/back skips and a five-minute seek retain direct play;
both VC-1 five-minute seeks landed within half a second of the target. All three films were
visually checked on the console, with no playback failures or recovery replans. These are bounded
playback/seek checks, not whole-film runs; system-decoder renderer-drop counts remain unavailable.

All builds, including production (`BaleDebugServer=false`), apply the wrapper-fork defaults from
`SoftwareDecodeTuning` to software AV1: `bale_decode_ahead_frames=16`, plus the fixed zero-copy keys
(`bale_d3d11_upload=6`, `bale_zero_copy=1`, `bale_film_grain_mode=2`).
Above 1080p, decode-ahead drops to 8 and BufferTime to 500 ms (see docs/gpu-film-grain.md section 8).
`bale_dav1d_max_frame_delay=6` is set only above 1080p (width > 1920 or height > 1080). Route
width/height follow the video codec's plan selection: original delivery uses `plan.Source`; other
deliveries use `plan.EffectiveRecipe`, falling back to `plan.Source` for each missing dimension.
Unknown size leaves delay unset. Hardware/system video routes set none of the three keys.
`bale_decode_thread_priority` remains unset in production; only the debug `prio` overrides below
can emit it. This is decode tuning, not new 4K capability evidence or a verified performance improvement.
All builds also set `MediaStreamSource.BufferTime` to **1000 ms for software video** (500 ms above
1080p, `Above1080pBufferMs`), using `SoftwareDecodeTuning.DefaultBufferMs`; FFmpeg system-video routes retain the wrapper's zero default.
A 3×3 interleaved console A/B on BvS 1080p AV1 found comparable CPU at 1000/2000 ms: averages
of 1.415/1.434 cores within a 1.31–1.56 run-to-run spread, with zero late frames for both.
Delivered-ahead was ~1.3/~2.3 s, not a renderer queue-depth measurement; start/seek buffering
was ~0.2–0.3/~0.5–0.7 s. The user prefers the 1 s default for less start/seek buffering;
these results do not establish a CPU improvement.

**Software AV1 path (wrapper 0020, `2.1.0-bale.20`; `bale_gpu` ABI 6, grain ABI 2, zero-copy ABI 1).**
There is one path. libdav1d decodes into `bale_gpu` zero-copy D3D12 buffers; the D3D12 direct write
(`bale_zc_repack_into` / `_into_bits`, fused GPU grain) fills the pooled MF texture, and a declined
10-bit write lands (`bale_zc_repack`) and runs the D3D11 grain pass (`bale_grain_gpu_*`). Claimed:
8- and 10-bit 4:2:0, Main profile, Series X|S only (`ZeroCopyRuntime.Register()` must succeed).
Grain is GPU only. A stream zero-copy can't take is refused at open, and any runtime zero-copy or grain
failure raises `NotifyError` once; either way the app replans to a server transcode. Removed: CPU grain
(`bale_grain_cpu_apply`), the staging upload and threaded swscale (`bale_sws_threads`), upload variants
1–5 and upload fault injection, the debug D3D11 repack (`bale_repack_*`), and the upload, D3D12 and
zero-copy probes (`bale_gpu_upload_probe`, `bale_gpu_d3d12_probe`, `bale_gpu_zero_copy_probe`). Older
measurements below that mention those paths are history.

`Audio.SystemDecoderAAC` and `Audio.SystemDecoderMP3` are enabled. Upstream FFmpegInteropX
**2.1.0 and master only have `SystemDecoderAAC` / `SystemDecoderMP3`**; there are no system
AC3/EAC3/PCM or encoded-passthrough switches. Wrapper patch 0021 (`2.1.0-bale.21`) adds
`bale_system_decoder_dolby`, which Bale sets on every FFmpeg mount: AC3/E-AC3 packets reach the
system Dolby decoder undecoded (like AAC), so E-AC3 JOC (Atmos) is no longer decoded to PCM by
FFmpeg. Atmos output itself is unverified (the developer has no Atmos receiver). TrueHD Atmos
still decodes in FFmpeg and loses its objects.

**Direct MKV/WebM always uses the FFmpeg demuxer** with system video and audio decoders. Windows'
MKV source over-read the 1080p H.264 Shrek MKV (~385 MB burst at start, then ~2.2x the bitrate)
and blocked on the network: 60 s A/B at the same point, native 211 waits / 33.7 s / 359 MB versus
FFmpeg 5 waits / 1.15 s / 110 MB, and visibly smoother on the console. The likely trigger is sparse
SRT subtitle tracks. MP4 (faststart, no subtitles) measured the same on both routes (10 vs 6
startup-only waits, 80 vs 76 MB in 60 s), so MP4 stays on Windows' source.

**VP9 always uses the FFmpeg demuxer**, in any container, with the system (hardware) VP9 decoder.
FFmpegInteropX hands VP9 packets to Media Foundation undecoded, as it does H.264 and HEVC
(`ForceSystemDecoder`; never FFmpeg's D3D11 or software VP9). The server files `.webm` as `mkv`,
so there is no separate WebM container claim, and VP9 is never offered over HLS. Evidence so far:
the decoder probe lists VP9 profile 0 and profile 2 up to 4K, and the IMFMediaEngine suite played
a VP9 clip through the system decoder. Direct play of a real VP9 library file on this route is
still to be verified on the console. A failed open or decode replans to a server transcode with
the VP9 claim withdrawn.

**Original-file audio selection:** only `original_http` advertises
`client_selected_audio_track_v1`, matching Android's `PlaybackCapabilityDetector.kt` and Apple's
`ApplePlaybackV3Capabilities.swift`. `selected_tracks.audio.index` is an ordinal in the selected
file's catalog audio list, not a global stream index. FFmpegInteropX moves its preferred audio
stream first; Bale restores catalog order by sorting `AudioStreams[].StreamIndex`, requiring the
complete catalog and mounted inventories to agree. The native MP4 path requires a unique match
by language, codec, channels and embedded name; Windows track IDs are opaque. Ambiguous native
identities are refused rather than selected by a guessed position.

`SelectOriginalAudioTrack` uses the shared Core selector to set and read back
`MediaPlaybackItem.AudioTracks.SelectedIndex` after MediaOpened, **before Play** (AutoPlay stays
off). Missing, ambiguous, unsupported or unconfirmed audio uses the existing single
`failure_recovery` replan with `audio_track_selection_failed`, the source position and the same
selected track identity. Recovery disables original delivery for this attempt; an unsupported
decoder also withdraws that audio decode codec so the server can convert it. A second failure
shows the normal playback error. Selected-track `OpenFailed` is handled the same way. The claim
does not expand decode, channel or passthrough capabilities. A server progressive remux has only
its selected audio stream, so its local ordinal is zero. There is no in-player audio picker yet;
the selector can be reused when the planned transport overlay adds one.

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
  [buffer=ms|default] [ahead=N|default] [delay=N|default] [prio=N|default] \
  [maxres=0|1|default] [dpb=N|default] [grain=dav1d|gpu|off|on|default]
```

No arguments reads the current process-wide overrides (`GET /ffmpeg-tune`); arguments update them
atomically (`POST /ffmpeg-tune` with the same query keys). Keys may be supplied in any order, once
each. Omitted keys keep their previous overrides; `default` clears only that key, restoring its
release setting. Restarting the process clears all overrides. Settings are captured at playback-attempt
creation, retained through recovery/remounts, and applied only to the next real FFmpeg Direct player
attempt, not an already open player, Native Direct, HLS or the standalone probe. `ffmpeg policy|off`
remains independent.

- `grain=dav1d|gpu` (software video only) sends `bale_film_grain_mode=0|2`; `on` aliases dav1d, and
  `default` clears the override, restoring production GPU grain. GPU grain is fused into the zero-copy
  direct write (or runs as the D3D11 pass on a landed 10-bit frame) and only ever writes Bale-owned
  textures, never dav1d's reference pictures. Unavailable or failed GPU grain fails the stream to a
  server transcode. `grain=off` retains the independent `bale_dav1d_export_film_grain=1` export-only
  measurement: no dav1d or Bale grain, even if both keys are supplied. This A/B facility works on
  authenticated library files, which the URL-only probe cannot open. `grain=cpu` was removed with CPU
  grain (wrapper 0020).
- Software AV1 is fixed to the zero-copy direct write: production always sends `bale_d3d11_upload=6`
  and `bale_zero_copy=1`. The former `upload=`, `uploadfault=`, `repack=` and `zerocopy=` knobs and
  upload variants 1–5 were removed (app 3b19c01, wrapper 0020); the scripts and app reject them.
  To verify delivery, select `scripts/bale-debug.sh frame-delivery managed` before opening the player,
  then read **Sample memory** in the stats overlay, `where` or `scripts/bale-debug.sh stats`. It reports
  `GPU texture (42/42 checked)`, `CPU buffer (42/42 checked)` or `mixed: texture 10 · buffer 32` from
  the first video sample and every 24th sample, resetting on each MSS Starting event (including seeks).
  Native/off observers and no successful checks show `unavailable (use frame-delivery managed)`; these
  are checked samples, not all delivered frames or proof of hardware decoding.
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
  (including zero) winning on any FFmpeg route; `default` restores **1000 ms for software video** and
  leaves system-video routes untouched (wrapper default: zero). This is the release setting chosen
  from the console A/B results above; it can retain substantial decoded-frame memory and is independent
  of demux read-ahead.
- `ahead=N` writes decimal-string `FFmpegOptions["bale_decode_ahead_frames"]`, inclusively **0–64**
  decoded frames; **0 = off**. This is native decoded-frame lookahead, not the packet read-ahead queue.
- `delay=N` writes `FFmpegOptions["bale_dav1d_max_frame_delay"]`, inclusively **0–64** frames;
  **0 = dav1d default**. It applies only to the libdav1d decoder.
- `prio=N` writes `FFmpegOptions["bale_decode_thread_priority"]`, inclusively **0–2**:
  **0 = off**, **1 = THREAD_PRIORITY_ABOVE_NORMAL**, **2 = THREAD_PRIORITY_HIGHEST**. It boosts
  the decode-ahead worker and newly created libdav1d workers. The release default
  is **unset** (no key emitted, no boost); this is a debug-only experiment, not production tuning.
- The fork consumes these three options itself, never forwarding them to FFmpeg. Numeric values use
  strict invariant decimal parsing (no signs, spaces, fractions or exponents); invalid native values
  return `E_INVALIDARG`. On software video, explicit overrides win over release defaults, including
  `ahead=0` / `delay=0` / `prio=0`. `default` removes only that override, restoring ahead=16,
  the size-dependent delay setting or an unset priority key. Description text appends
  `ahead=N delay=N prio=N` only for explicit overrides. Hardware/system video ignores these
  three overrides; the input/read-ahead options remain available on all real FFmpeg routes.
- Production (`BaleDebugServer=false`) applies the release software-video defaults, never the
  memory-only debug overrides. The A/B facility does not change the production decoder allowlist
  or establish a verified performance improvement.

For an isolated comparison, close the current player, set the baseline, then open the same title:

```sh
scripts/bale-debug.sh ffmpeg-tune readahead=default readsize=default prefetch=default \
  buffer=default ahead=default delay=default prio=default
# After closing the baseline player, change only native read-ahead and reopen:
scripts/bale-debug.sh ffmpeg-tune readahead=on
# Optional, separate experiments (close/reopen after each change):
scripts/bale-debug.sh ffmpeg-tune readsize=65536
scripts/bale-debug.sh ffmpeg-tune prefetch=8
scripts/bale-debug.sh ffmpeg-tune ahead=8
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
  [threads=N] [10bit=on|off] [rate=R] [ahead=N] [delay=N] [prio=N] \
  [maxres=0|1|default] [dpb=N|default] [hdr=on|off|default] [grain=dav1d|gpu|off|on|default]
```

Supply seconds/mode before tuning, then keys in any order, at most once each. Seconds are 1–60
(default 3), threads are 0–16 (0 = logical CPU count), 10bit is on/off, and rate is an invariant
0.5–2.0 (default 1). The fork keys have the same ranges as above; software-mode probes use the release
ahead=16 default and the production zero-copy path (GPU grain) when omitted. URI probes have no playback plan/dimensions before creation,
so delay remains unset unless explicitly overridden (use `delay=6` for a known above-1080p comparison).
Priority remains unset unless `prio=0..2` is supplied, with the same levels as above. These three
fork options affect only software video, including an auto-mode probe that selects software decoding;
hardware/system video ignores them. Auto/system modes retain wrapper defaults for omitted keys.
`hdr=on|off` forces the wrapper's `Video.HdrSupport` to Enabled/Disabled (omitted or `default` keeps
Automatic, which applies HDR colour info only when the HDMI mode is already BT.2020); the report adds
`HDR support:` and the video stream's `HasHdrMetadata` / `IsHdrActive`. Production never sets it.
`grain=off` sends `bale_dav1d_export_film_grain=1` (wrapper patch 0005, `2.1.0-bale.5`+; older wrappers
reject the key and the open fails): libdav1d skips film-grain synthesis and exports the parameters as
frame side data, so a grain-carrying AV1 clip can be timed with and without grain (`Film grain:` in the
report). `dav1d`/`on` explicitly send `bale_film_grain_mode=0` and `gpu` sends 2; `default`/omitted send no
grain key, so a software-mode probe keeps production GPU grain. (Before wrapper 0020, `cpu` sent 1 and
the probe kept dav1d and CPU buffers unless `grain=gpu` opted it into upload variant 1.)
Console check on 0.1.291.0 (bale.5, 20 s `software`): the key is accepted and reported as exported.
The public Jellyfin AV1 clips carry no grain (`film_grain_params_present = 0` in the 1080p and 4K
sequence headers), so the 1080p 10 Mbps run is unchanged (ratio 0.992 / 1.87 cores with synthesis,
0.998 / 1.90 exported).

**GPU grain, stage 1 (wrapper patch 0006, `2.1.0-bale.6`; staging upload removed by 0020).** D3D11 texture delivery supports software
NV12/P010 output on the real-player route. **Upload variant 1 was then the default for software video
in all builds (variant 6 since 2026-11-09, see zero-copy direct write); `upload=off` is the debug escape hatch.** The existing
copy/sws output step writes directly into a three-texture STAGING ring, then CopyResource + Flush
uploads to pooled DEFAULT textures on the MSS renderer's multithread-protected device. Samples use
`CreateFromDirect3D11Surface`; Processed returns textures, and seek/size changes discard the old pool
without reusing MF-held surfaces. A device/texture/map/surface failure permanently falls back to CPU
buffers for the stream, including the failing frame. With `grain=default` this remains an upload-only
CPU/visual A/B using dav1d grain; patch 0009's Rust grain is opt-in. `grain=off` still removes grain,
independently. The owner's console
verification found all GPU textures, zero late frames, lower CPU and pixel-identical output;
CI only builds/checks this path.

**Early upload failure / patch 0007 (`2.1.0-bale.7`).** The owner's bale.6 console test of 1080p AV1
10-bit → P010 fails about **0.1 s** after open with **0x80070057 (`E_INVALIDARG`)** when upload is on;
upload off plays. Native and managed frame observers both reproduce it, and the wrapper's synchronous
CPU fallback does not trigger. This suggests asynchronous MF rejection of the delivered texture sample,
not a proven cause at that point. Patch 0007 exposed five DEFAULT texture flag variants (the hardware
path preserves DECODER bind flags; bale.6 used SHADER_RESOURCE only). Unsupported variants fall back
to CPU on allocation failure.

**Console findings / Processed lifetime fix (`2.1.0-bale.8`, patch 0008).** The owner's subsequent
bale.7 tests (patches 0006 + 0007, 1080p AV1 → P010) established:

- SHADER_RESOURCE only (bale.7 **1**, now **4**) and SHADER_RESOURCE + RESOURCE_MISC_SHARED (**5**)
  fail playback in MF with `E_INVALIDARG` after the first couple of samples.
- DECODER | SHADER_RESOURCE (**2**, bind `0x208`), DECODER (**3**, `0x200`) and
  RENDER_TARGET | SHADER_RESOURCE (bale.7 **4**, now **1**, `0x28`) are accepted as texture samples;
  playback stays **Direct play with zero late frames**.
- In every variant the uploader nevertheless disables itself after **1–2 samples**, logging
  `Bale D3D11 upload OnProcessed failed (hr=0x80000013)` followed by
  `upload disabled (hr=0x80000013); using CPU buffers`. `0x80000013` is **RO_E_CLOSED**:
  MF has already closed the sample's surface when Processed tries to read `sender.Direct3D11Surface()`.
  Continued Direct play is therefore not proof of sustained GPU delivery or recycling.

Patch 0008 stores the pooled texture, Processed event token and diagnostic timestamp with each sample
when the uploader issues it. OnProcessed looks up that record, removes tracking, detaches the handler
and returns the stored texture; it never reads the closed surface or timestamp. Unknown callbacks
(already flushed) are no-ops. Flush detaches handlers and drops stored/pool textures, never recycling
across seek/size generations; returns also drop textures whose full descriptor no longer matches.
Callback/detach exceptions are logged **once** without setting a failure flag or disabling upload;
real synchronous D3D/MF failures (including Map, CreateTexture2D, device removal and
CreateFromDirect3D11Surface) retain permanent CPU fallback. No decoder, staging or grain change.
Variant **1** is now RT|SRV, ready for patch 0009's opt-in GPU grain rendering into the uploaded texture.

The first-three diagnostics below remain. Additionally, once the uploader has issued **240 texture
samples** (including decoded lookahead, not just actual MSS handoffs), it logs one snapshot:
`Bale D3D11 upload steady: issued=N processed=M pooled=P`. N/M are uploader-lifetime totals, retained
across Flush; P is the current free pool size. Use that together with **Sample memory** to check sustained
texture delivery/recycling and bounded in-flight textures. Seek/size changes still need the owner's
console verification; no console or server was accessed for this default change.

Debug builds now forward **only** native messages starting exactly `Bale ` to Bale's log as `ffmpeg: …`
(trailing newlines trimmed), with Info enabled. Everything else is dropped before logging because
FFmpeg may include media URLs; production retains `SetLogProvider(null)`. Patch 0007 prefixes every
new diagnostic `Bale D3D11 upload`: acquired device flags/feature level/multithread protection,
declared output W×H/subtype, recreated pool W×H/DXGI format/bind/misc flags, first three actual MSS
sample handoffs (index/position/texture size), first three Processed callbacks, and failure HRESULTs.
CopyResource/Flush return void, so their failure diagnostic uses GetDeviceRemovedReason.

The upload device-manager handle is independent of hardware decoding: partial initialization failure,
device reset and source teardown close it; normal teardown first stops the decode-ahead worker.
No AVHWDeviceContext is created and the decoder remains FFmpeg software. `ID3D10Multithread`
protection is enabled and verified on the shared device/context, not restored on teardown because
MF shares that context. Only a tracked sample's Processed event returns its texture; seek/format
changes detach old handlers and discard pool references, leaving MF's surface references alive.
These are the implemented lifetime rules, not runtime proof: device reset, seek/format changes,
Processed timing, NV12/P010 presentation/colour parity and CPU cost still need console validation.

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

Bale uses `FFmpegInteropX.UWP.Lib` **2.1.0-bale.9** with the custom x64 native package
`FFmpegInteropX.UWP.FFmpeg` **8.1.2-bale.dav1d.1.5.4** (now **.p1**, which adds the
`scripts/ffmpeg-native-patches` set; see "Zero-copy design"). The public 8.1.2 x64 `avcodec-62.dll`
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

Bale requires exactly `[2.1.0-bale.25]` of `FFmpegInteropX.UWP.Lib`, from the fork's immutable
release [`wrapper-ffmpeginteropx-2.1.0-bale.25`](https://github.com/Zorro92/bale-ffmpeginteropx/releases/tag/wrapper-ffmpeginteropx-2.1.0-bale.25),
built from fork commit `f07840f4728fa5552b593ebdeb3f7060b5578f63`. Its nupkg SHA256 is
`da366a3e7e03d3e57de5caf26999cce750e6b3bd9d16b33bf8874fdc19748ad9`; its NuGet content hash is
`8jJxWq56HCZIXCywpc1bXfea1U/p1gh2fUm8Gyz3hJKTBy7k3SrEZOsTWQL3W6G71Xyfh+dLo0nUDeCAQM9GgA==`.
Both were verified against the downloaded package, including NuGet PackageArchiveReader's content
hash. The fork's `Source/bale_tonemap.h` is byte-for-byte identical to Bale's ABI 1 header.
Version .25 adds the opt-in GPU tone-map stage for d3d11va 10-bit PQ streams (`bale_tonemap=0/1/2`),
retaining .24's HDR side-data fixes. Self-test mismatch stops the stream with `0x8004BA01`.
The debug `tonemap=` switch is now available; production leaves the key absent and keeps its
server tone-mapping policy.
Bale no longer carries or applies wrapper patches or builds/releases wrapper packages. Changes go
as commits to [Zorro92/bale-ffmpeginteropx](https://github.com/Zorro92/bale-ffmpeginteropx); its
[`bale/README.md`](https://github.com/Zorro92/bale-ffmpeginteropx/blob/main/bale/README.md) is the
build/release procedure. The fork's CI forbids IDL/project changes and compares built/original winmd
public type names. Native FFmpeg and its build/release workflow remain in Bale unchanged.

The release repository, source commit, tag, SHA256 and NuGet content hash are pinned in
`scripts/ffmpeginteropx-wrapper.json`; both framework/RID lock entries agree. `restore-ffmpeg-native.ps1` downloads it into isolated staging, verifies before moving
it to `.cache/ffmpeg-native`, and clears its cached extraction. Source mapping allows the wrapper
only from that verified feed, never nuget.org. `Licenses/FFmpegInteropX/SOURCES.txt` ships in debug
and production MSIX files next to the license texts. The package checker compares the actual MSIX
`FFmpegInteropX.dll` with `runtimes/win-x64/native/FFmpegInteropX.dll` in the hash-pinned nupkg, and
checks that the shipped wrapper provenance is identical too. Activation/factory and dav1d/license
checks remain enabled in both configurations.

**Historical patch-series era (through `2.1.0-bale.22`).** The following build and playback
notes describe the former Bale-owned wrapper pipeline; the patch numbers now refer to commits
in the fork. Release `2.1.0-bale.9` was built from Bale commit `0b9a460220dfc35c7097f5c632b5cd97b51532b0`
by successful [wrapper run 42513374008](https://github.com/Zorro92/bale-for-silo/actions/runs/42513374008).
Independent verification matched all six release assets' GitHub SHA256 digests, all nine ordered patch
hashes in `SOURCES.txt`, the original/native package pins, all 130 tracked `Source/` and `Build/` files
in the corresponding-source archive, and the nine unchanged original-package entries (including the
original winmd, managed projection and x86/ARM64 binaries). The 1,344 additional archive source files
are confined to C++/WinRT's `Source/Generated Files/` output. The built/original public type-name check
reports 29 identical types. The NuGet content hash in the pin/lock entries was obtained with NuGet's
`PackageArchiveReader.GetContentHash` API, not inferred from the archive SHA512.
The corresponding-source archive SHA256 is
`9b4682596c14c33bdcee78b6ded2a0ebb2283581f898f4f7622ea80ab4a06cf0`.

The former manual `ffmpeginteropx-wrapper.yml` workflow built only the **Release_UWP x64** native
wrapper. That workflow and its patch series have been removed from Bale. The fork now owns the
build; producing a release there does not automatically update Bale's app pin.

**Step 4 (`2.1.0-bale.9`, patch 0009; CPU grain removed by 0020):** this release adds opt-in Rust CPU/GPU grain
without changing the default from dav1d. Selection precedes `avcodec_open2`; compatible software
libdav1d NV12/P010 output and the complete grain ABI are required, and GPU additionally needs upload
variant 1. The 0005 export-only switch retains precedence. Grain modifies only Bale-owned output,
with permanent CPU fallback after GPU failure, and scaled output is shown ungrained. App CI now
checks all six grain exports in the actual packaged `bale_gpu.dll`. See `docs/gpu-film-grain.md`,
section 6, for the mapper, output paths, failure handling and serialized-owner teardown contract.
Console playback parity, CPU/late-frame A/B and memory checks remain pending; include CPU mode with
upload on, whose grain reads mapped CPU_ACCESS_WRITE staging that may be write-combined.

The former build pin in `scripts/ffmpeginteropx-wrapper.json` recorded upstream commit
`88428c66e106852338b48554a4a00c9e48da588f` and the original nuget.org 2.1.0 package SHA256
`927f23e66688b6c86659121fd20c12c44507581b3b00de5437274741bb506881`. The source checkout omitted
submodules. Native FFmpeg came only from the hash-pinned `restore-ffmpeg-native.ps1 -NativeOnly`;
upstream's `8.1.2-pre01` packages were never restored. Only the vcxproj's three build-tool packages
were restored from nuget.org. Native MSBuild used `Release_UWP`, `x64`, toolset `v145`, SDK
`10.0.26100.0`, minimum `10.0.17763.0` and `LibraryVersionNumber=2.1.0.0`. The pipeline bypassed
upstream's full build/restore entrypoint, retaining the managed projection and overriding the
native FFmpeg package version. These build pins and responsibilities now live in the fork.

The former `0001-decode-ahead-threaded-sws.patch` introduced opt-in decoded-frame lookahead,
threaded swscale and libdav1d max-frame-delay. `0002-decode-thread-priority.patch` added optional
priority boosts for decode-ahead, libdav1d and swscale workers. Patch 0020 removed threaded swscale
and now rejects `bale_sws_threads`; the three remaining decimal-string `bale_*` keys are documented
above, and production never sets the priority key. The old pipeline applied patches with
`git apply --index` in ordinal order, allowing only `Source/` `.cpp`/`.h` changes. The fork builds
those changes directly as commits, with its source guard protecting IDL/project files. The built
winmd's public type-name set must still match the original. This is a minimum metadata guard,
not a complete member/ABI comparison.

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
of setting (also with sws=1).

**1080p late-frame bursts are a probe startup artifact (0.1.350.0, 2026-11-09).** 5 s probes show the
same ~146–149 late frames in 3 of 8 runs, so the whole burst falls in the first ~2.5 s. Burst runs
reach a minimum delivered-ahead of 0 s; clean runs keep ≥0.04–0.09 s. `ahead=48` still burst (1 of 5
vs 3 of 5 at 16) and left the median delivered-ahead unchanged (~0.096 s): MF's own pull sets that
horizon, not the decode-ahead queue. The probe leaves `MediaStreamSource.BufferTime` at the wrapper's
0, while the player sets 1000 ms at 1080p. Seven real-player starts of a 1080p AV1 library movie as
Bale Test had 0 late frames (`frame-trace` late=0) with ~1.3 s delivered ahead. Decode-ahead stays 16.

**In-loop filter cost on a real 4K title (desktop, 2026-11-09).** dav1d 1.5.4 with asm,
`--inloopfilters`, 90 s heavy stretch of a 3840x2076 10-bit library movie (~16 Mbps, film grain
applied by the CLI), i7-10610U, interleaved:

| Threads | all | no CDEF | no restoration | deblock only | none |
|---|---|---|---|---|---|
| 4 (2 reps) | 24.2 fps | 25.9 | 25.6 | 27.1 | 28.7 |
| 1 | 12.0 fps | 13.1 | 13.4 | — | — |

CDEF is ~6–9 % and loop restoration ~5–11 % of decode time here (the Jellyfin/SVT clips: CDEF 12–32 %,
restoration ~0). Moving CDEF/LR to the GPU would therefore gain at most ~11–20 % on real content, and
CDEF output is a reference for later frames, which conflicts with dav1d's frame threading.

**Per-thread CPU at 4K on the console (0.1.354.0, 2026-11-09).** Real player, same title resumed at
~10 min, software AV1 (`av1-claim 2160`), app mode, `bale-debug.sh threads 20` three times:

| Group (thread start module) | Threads | Cores |
|---|---|---|
| `ucrtbase.dll` (dav1d workers; the names don't reach `GetThreadDescription`) | 9 | 6.22–6.35 |
| `avutil-60.dll` (FFmpeg slice thread pool, likely swscale) | 3 | 0.56–0.64 |
| `ntdll.dll` (thread pool: MF, audio, I/O) | ~60 | 0.23–0.27 |
| UI thread (`ApplicationView ASTA`) | 1 | 0.11–0.15 |
| process total | ~85 | 7.26–7.37 of 8 |

dav1d is ~86 % of the process. Everything else is ~1 core, and ~0.6 of it is the avutil pool.
The process nearly fills all 8 logical CPUs. The session buffered 30 times (2.7 s) with 0 late frames.

The avutil pool is swscale: production runs `sws=4` (caller plus 3 workers). With `ffmpeg-tune repack=on`
(resumed at ~11 min, 2 × 20 s) it disappears. dav1d's group then rises to 6.4–6.9 cores, and the process
stays at 6.9–7.3, so dav1d absorbs the freed CPU. Buffering was 41 times (4.8 s) in a different scene,
so the two runs aren't comparable. The earlier controlled A/B (gpu-film-grain.md section 8) showed the GPU
repack stalls more. Freeing this ~0.6 core needs the repack's GPU work off the decode thread's
immediate-context lock first.

**Repack A/B, interleaved (0.1.354.0, 2026-11-09; debug repack removed by 0020).** Start Over, 120 s, 3 runs each, alternating:

| Path | Buffering at 120 s | Position reached | Process CPU (80–100 s) | dav1d + decode-ahead group |
|---|---|---|---|---|
| swscale (production) | 32–35 times, 4.6–5.5 s | ~113 s | 6.98–7.05 cores | 6.00–6.06 |
| GPU repack | 68–75 times, 10.3–11.0 s | ~107 s | 6.64–6.82 cores | 6.20–6.39 |

The repack doubles the stalls again and the process uses ~0.25 cores *less*, so something waits
instead of working. Single-threaded swscale (`sws=1`) did not hurt earlier, so the single-threaded
planar copy alone is an unlikely cause. The suspects are the extra GPU work (staging copy, two
repack draws, grain from a scratch copy) submitted under the multithread lock, and `Map` waits on the
three-texture staging ring. The `ucrtbase.dll` group includes the decode-ahead thread (std::thread),
so the per-thread tool cannot separate it from dav1d.

**Stage timing (wrapper `2.1.0-bale.13`, patch 0013, log-only).** Averages per 240 frames on the
decode-ahead thread at 4K: GPU repack and grain ~1 ms (~0.35 ms without grain), `Map` ~1–2 ms, `Flush`
~1 ms, sample creation ~0.2 ms. The lock and the GPU are not the cause. The repack's single-threaded
copy of dav1d's planes into the staging texture took **23–40 ms per frame**, against 9–17 ms for
4-thread swscale, and dav1d sat idle waiting for it.

| Title, wrapper | Repack on | Repack off (swscale) |
|---|---|---|
| Brawl (grain), bale.13 | 71–76 times, ~10.7 s | 31 times, 5.2–5.7 s |
| Shrek 4K AV1 (no grain), bale.13 | 46 / 66 times, 6.7 / 10.1 s | 10 / 8 times, 1.8 / 1.3 s |
| Brawl, bale.14 (sliced copy) | 48 times, 8.0–9.4 s | 30–31 times, 5.2–5.3 s |
| Shrek, bale.14 (sliced copy) | 13 times, 2.2–2.7 s | 8–9 times, 1.5–2.4 s |

**Sliced copy (`2.1.0-bale.14`, patch 0014).** The planar copy is split by rows over `SwsThreads`
(caller plus workers, same priority as swscale). The copy drops to 11–16 ms, the same as swscale, and
stalls fall by a third (Brawl) to three quarters (Shrek). Repack is still slightly behind and saves no
total CPU (process 7.6–7.7 cores on against 7.45–7.5 off on Shrek). The conversion was never the real
cost: writing ~24 MB per 4K frame into the staging texture runs at only ~2 GB/s with 4 threads, either
because staging memory is write-combined or because dav1d competes for memory bandwidth. Repack stays
a debug knob.

**Upload probe (`bale-debug.sh upload-probe`, since removed; 0.1.364.0, Series X ("arden" GPU), 4K R16 frame = 24.9 MB).**
The memory type is not the problem; the console's state while the player is open is.

| ms per 4K frame (4 threads unless noted) | Idle (detail page) | Player open: 1080p H.264 HW, playing or paused | 4K AV1 paused |
|---|---|---|---|
| ALU chain 1T / 4T | 22.7 / 22.9 | 28.6 / 29.2 | 28.8 / 29.5 |
| L2-resident copy 1T | 0.45 (55 GB/s) | 0.57 (44 GB/s) | 0.56 |
| RAM copy 1T / 4T | 3.3 / 2.1 | 18–19.5 / 8.1–8.9 | 14.9 / 7.8 |
| Staging write (production) | 2.05 | 8.3 | — |
| Staging write, streaming stores | 1.44 | 5.6 | — |
| UpdateSubresource / DYNAMIC discard | 1.9 / 2.2 | 9.0 / 9.1 | — |

- Idle, every upload method runs at RAM speed (~12 GB/s, streaming stores ~17 GB/s), and reads from
  staging run at 12–16 GB/s, so staging memory is cached, not write-combined.
- About 10 s after the last controller or keyboard input, the console drops into a slow state: CPU
  work is ~25% slower (clock) and DRAM copies 4–6x slower. Any input restores full speed at once, and
  it falls back 10–12 s later (two runs, 1 s resolution). A key the UI ignores (F24 or Shift through
  `InputInjector`) is enough. The state is console-wide: it shows on any page, after a full app
  restart, with or without playback. An earlier reading tied it to the player page only because
  probes there ran after a quiet stretch and leaving the player meant pressing Back.
- During real playback nobody touches the controller, so a whole film runs in the slow state. The
  ~2 GB/s fill is this bandwidth limit, which also caps dav1d itself. Faster converters (libyuv
  instead of swscale) cannot help; fewer bytes moved can: streaming stores (~1.5x) or not copying.
- A/B on Shrek 4K AV1 (0.1.364.0, Start Over, 120 s, swscale, interleaved 3+3), no input versus an
  injected Shift every 5 s. Shift leaves the screen unchanged. F24 must not be used: the Xbox shell
  opens the Guide's Capture panel on it.

  | | Buffering | CPU |
  | --- | --- | --- |
  | no input | 9 times, 1.4–2.2 s | 6.1–6.8 cores |
  | Shift every 5 s | 1 time (start), 0.1 s | 1.8–2.2 cores |

  So with fresh input, 4K AV1 plays smoothly on about two cores. Every earlier 4K CPU and stall
  figure in this document was measured in the idle state.
- Same result on Brawl 4K AV1 (0.1.441, zero-copy, from 55 s, 12 s window, interleaved runs,
  2026-11-10). Prefetch 8 stops new stream waits but not the stalls, so they are decode-bound, not
  I/O-bound; dav1d's default frame delay (instead of 6) is far worse.

  | | Buffering in window | CPU | Late frames |
  | --- | --- | --- | --- |
  | no input, defaults (2 runs) | +14, +11 | 4.6–5.9 cores | 0 |
  | no input, prefetch 8 (2 runs) | +15, +17 | 4.6–6.0 cores | 0 |
  | no input, frame delay default (2 runs) | +59, +69 | 4.0–4.6 cores | 0 |
  | Shift every 5 s | +0 | 1.2–1.7 cores | 0 |
- Open: whether an app-level API holds the fast state. Ruled out with `bale-debug.sh idle-test` (0.1.368):
  - The player's held `DisplayRequest` does not.
  - A Kodi-style `DisplayRequest` `RequestActive` + `RequestRelease` pulse does not wake the console or reset the timer.
  - Media session (SMTC) `PlaybackStatus = Playing` does not, on the view or on the player. The player's `MediaPlayer` already reports Playing during playback, and the console still slows after ~15 s with no input.
  - A WebView2 (Chromium, the engine Jellyfin's Xbox app runs in) playing a muted 720p H.264 loop does not either (`bale-debug.sh webview-test`, 0.1.370). The probe was slow after 15 s and 20 s idle while the video kept playing, and fast again after one key.
  - `expandedResources` is not an option for a Store app: Microsoft's `ReleaseExclusiveCpuSets` page says an Xbox-targeted UWP with it "will be rejected in certification", and on Xbox it means running with a Game title's resources.

**D3D12 in app mode (same probe).** Hardware device OK, max feature level 11_0, UMA and
cache-coherent UMA true, existing heaps reported supported and row-major cross-adapter textures true,
but `OpenExistingHeapFromAddress` on a 32 MB `VirtualAlloc` region fails with E_INVALIDARG. A
cache-coherent UMA `CUSTOM` heap (write-back CPU pages, L0) is the other zero-copy candidate: D3D12
allows a resource to stay mapped while the GPU reads it, so dav1d could decode straight into it.

**Zero-copy probe (`bale-debug.sh zero-copy-probe`, since removed; 0.1.372, 2026-11-09): every check passes.**
- The CUSTOM write-back L0 buffer creates and stays mapped. The UPLOAD and READBACK heaps are also
  write-back L0 on this console.
- CPU speed on it equals ordinary RAM, for both writes and reads, in both states. Per 24.9 MB frame,
  4 threads: fill 2.0 ms and read 1.2 ms right after input; 7.3 ms and 2.9 ms after 15 s idle (RAM:
  8.1 and 2.9). So dav1d would not slow down by decoding into it.
- The GPU copies it in 0.8 ms (~31 GB/s) and 0.86 ms after 15 s idle. The input-idle slowdown hits
  the CPU only, not GPU memory access.
- The GPU sees plain cached CPU stores without Unmap or flushes (24.9 MB round trip matched).
- A D3D12 shared P010 texture and shared fence open in D3D11 (`OpenSharedResource1`,
  `OpenSharedFence`). The D3D11 context waits on the fence and reads back exactly what D3D12 copied
  (both planes); copy, wait and readback take 2.5–3.3 ms.
- P010 in D3D12 (Support1 0x3A80C320): render target, typed UAV, shader load and decoder output are
  all supported, so a compute or pixel repack shader can write the planes directly.
- What remains unbuilt: an FFmpeg `libdav1d.c` allocator patch (native rebuild), the repack shader
  from dav1d's planar layout into P010, the Media Foundation handoff, and fences guarding buffer reuse.
  Series X|S only; Xbox One app mode has no D3D12 and keeps today's path. Store certification of a
  D3D12 app (not game) is unverified.

**Zero-copy design (approved for build 2026-11-09; production default since 2026-11-09 for 10-bit software AV1, Series X|S only).**
Goal: dav1d decodes straight into GPU-readable memory, and a D3D12 compute shader writes P010, so the
swscale conversion (~0.6 core) and the staging fill disappear. dav1d's own cost is unchanged.
1. **FFmpeg patch** (`libavcodec/libdav1d.c`, native rebuild). FFmpeg's dav1d allocator always uses
   its private `av_buffer_pool`. The patch: if the caller replaced `AVCodecContext.get_buffer2`, ask
   it for a `yuv420p10` frame of the 128-aligned size first. Accept the result only if it is one
   buffer (`buf[0]` only, all planes inside it), 64-byte aligned data and strides, and
   `linesize[1] == linesize[2]`; otherwise, or on any error, use the original pool for that picture.
   Output code stays as it is (`frame->buf[0]` is the picture's buffer reference). libdav1d has no
   DR1 flag, so the wrapper's existing DR1 hook never sets `get_buffer2` for it today.
   `build-ffmpeg-native.ps1` gains a reviewed patch step (only `libavcodec/libdav1d.c` may change),
   a new version suffix and release tag, and the patch is recorded in `SOURCES.txt`; the source
   archive is taken from the patched tree, as the LGPL requires. Done:
   `scripts/ffmpeg-native-patches/0001-libdav1d-user-picture-buffers.patch`, patch set `p1`.
   Checked on Linux against FFmpeg 38b88335 + dav1d 1.5.4 (Brawl 4K 10-bit, 8 threads, grain
   exported and applied; Jellyfin 4K, 1 thread): a valid custom buffer, a misaligned layout and a
   failing callback all give bit-identical frames to the default pool, every buffer is freed, and
   at most 10 pictures are alive at once (~250 MB at 4K, same as the internal pool).
2. **`bale_gpu` zero-copy module** (Rust, separate zero-copy ABI 1; `ABI_VERSION` unchanged then; now 6).
   Done: `native/bale-gpu/src/d3d12_repack.rs` (`bale_zc_*` exports), layout checks and CPU reference
   in `src/zc_frame.rs`, shader `shaders/repack_zc_cs.hlsl`, WARP tests `tests/zero_copy_*.rs`. One
   handle per uploader, created from the MF D3D11 device and immediate context after the caller
   registered its `d3d12.dll`: a D3D12 device on the D3D11 device's adapter, a compute queue, two
   shared fences (D3D12→D3D11 "repacked", D3D11→D3D12 "released") opened in D3D11 once. Create runs
   a readback self-test (both slots, then a size change with an odd chroma width).
   - Buffer pool: CUSTOM write-back L0 buffers, persistently mapped, sizes rounded to 64 KiB, at
     most 16 per handle. `alloc` (any thread) returns a CPU pointer and an opaque odd id; `release`
     (any thread, also after destroy) returns it. A released buffer is reused only once "repacked"
     passed the last dispatch that read it, so the caller may drop the AVFrame right after repack.
     A full pool waits at most 100 ms, then fails; the caller falls back to FFmpeg's own pool.
   - Landing ring (changed from the earlier "target pool"): two P010 textures created in D3D12
     (`HEAP_FLAG_SHARED`, UAV), each opened in D3D11 once. Repack returns a borrowed D3D11 texture
     valid until the next repack; the caller copies (or grains) it into its own pooled texture, so
     MF never holds a shared texture and only the caller's copy has to finish before reuse.
   - Repack: `cs_5_0` compute shader (compiled by fxc like the D3D11 shaders; root signature built
     in code). Raw-buffer SRV on the picture, typed `R16_UINT` / `R16G16_UINT` UAVs on the P010
     planes; each thread converts a 4×2 luma block and its 2 chroma pairs (`value << 6`).
3. **Per frame** (decode-ahead thread, wrapper patch 0015): if the AVFrame's buffer belongs to the
   pool, skip swscale and staging. Repack: the D3D11 context signals "released" for the previous
   landing texture (after the caller's copy from it); the D3D12 queue waits on "released" before
   writing a landing texture again, transitions, dispatches, returns it to COMMON and signals
   "repacked" N; the D3D11 context waits on N. Then the wrapper copies (or grains) the landing
   texture into its pooled texture and continues to `CreateFromDirect3D11Surface` as today. CPU
   waits in the module flush the D3D11 context first, so an unflushed "released" signal cannot
   deadlock. A buffer not from the pool (`S_FALSE`) takes today's path for that frame; any repack
   failure poisons the handle (later allocs fail, so dav1d returns to its own pool) and the wrapper
   takes today's path from then on.
   Built in wrapper 2.1.0-bale.15 (native `p1`): former patch `0015-zero-copy-landing.patch`,
   option `bale_zero_copy` (only with `bale_d3d11_upload`). The hook is installed in
   `SelectOutputFormat` (dav1d asks per picture, so before the first decode is enough) and declines
   until `SetUploadDevice` has created the handle. The repack size is the 4-aligned output texture
   size, read from dav1d's 128-aligned picture rows, so the landing texture matches the pooled
   texture for `CopyResource`. A refused layout (`E_INVALIDARG`) takes today's path for that frame
   only; other repack failures and every upload failure destroy the handle for the stream.
   Landing textures are created by **D3D11** (`SHARED_NTHANDLE | SHARED`, SRV + UAV binds) and
   opened in D3D12 with `OpenSharedHandle`. A CI matrix on both WARP device kinds (2026-11-09)
   showed WARP's D3D11 opens **no** D3D12-created shared texture of any format (P010, NV12,
   R16_UNORM, RGBA8, with or without UAV/simultaneous-access flags: `E_INVALIDARG` from
   `OpenSharedResource1`), while every D3D11-created one opens in D3D12, P010 with UAV included
   (D3D12 sees `ALLOW_UNORDERED_ACCESS | ALLOW_SIMULTANEOUS_ACCESS`). If a device refuses the UAV
   variant, the handle switches to a private UAV landing texture plus a GPU `CopyResource` into a
   D3D11-created SRV-only shared one, for its whole life; `landing_copies` in the stats counts
   those frames, so the console A/B shows which path the hardware takes. WARP tests cover both
   modes (the copy mode through a test-only constructor).
4. **8-bit AV1:** `bale_zc_repack_into_bits(..., bit_depth, grain, texture)` (bit depth 8 or 10;
   `bale_zc_repack_into` stays and is the 10-bit case) reads 8-bit planar yuv420p (strides and
   offsets in bytes) and writes the same P010 texture as `v << 8`, plain or with fused grain, through
   separate 8-bit PSOs (`/D BALE_8BIT=1` builds of the two zero-copy shaders). Create self-tests
   them separately: an 8-bit failure only declines 8-bit frames (S_FALSE, today's path), never
   10-bit. Grain parameters of the other depth are declined. **Not in scope yet:** 12-bit AV1 (keep
   today's path), Xbox One (no D3D12 in app mode).
5. **Test:** `ffmpeg-tune zerocopy=on`, then interleaved A/B against today's path on 4K AV1 in the
   input-idle state (CPU, stalls, late frames), plus a readback check of the first frame against
   swscale output.
   **Result (0.1.384.0 + script fix, Series console, Shrek 4K 10-bit AV1, Start Over, 120 s, no input,
   interleaved on/off/on/off, 2026-11-09):** the console takes the direct landing path
   (`landing_copies=0`, self-test passed, 0 alloc failures or waits, 2831–2860 frames repacked per
   run).

   | | zerocopy=on | today's path |
   |---|---|---|
   | Buffering | 3 / 0.6 s, 3 / 0.3 s | 9 / 0.9 s, 9 / 1.6 s |
   | Stats CPU (whole run) | 5.58, 5.56 cores | 6.09, 6.38 cores |
   | Process CPU at 80–100 s | 6.24, 6.21 cores | 7.52, 7.41 cores |
   | dav1d workers at 80–100 s | 5.88, 5.85 cores | 6.53, 6.46 cores |
   | `avutil-60.dll` (swscale) pool | gone | 0.61, 0.58 cores |

   Late frames were 0 in every run. Zero-copy saves ~1.2 cores: the swscale ~0.6 core, plus ~0.6
   core in dav1d itself, likely because the CPU no longer competes for the throttled memory
   bandwidth with the swscale pass. Paused-frame screenshots at 1503 s (stats pane open, uncovered
   edges compared) and 2406 s (pane hidden, full frame) are **bit-identical** on vs off.
   **Grain A/B (Brawl in Cell Block 99, 4K 10-bit AV1 with film grain, same method, zero-copy+GPU
   grain / zero-copy without grain / today's path, interleaved ×2):** buffering 20/2.1 s and 18/1.7 s,
   17/1.8 s and 15/2.0 s, 36/6.3 s and 40/7.0 s; process CPU at 80–100 s 6.40/6.28, 5.88/5.88,
   6.90/6.91 cores; 0 late frames; `landing_copies=0`. GPU grain works through zero-copy. It still
   costs ~0.45 core, almost all in the dav1d worker group (5.9 vs 5.5), although its per-frame CPU work
   is tiny. Likely cause: the D3D11 grain pass copies the landing texture into an intermediate and
   then reads it again, two extra 4K frame passes of shared memory traffic that slow the CPU in the
   bandwidth-capped idle state.
   **Copy skipped (0.1.388, 7ea29d4):** the grain pass now samples SRV-bound DEFAULT sources (the
   landing texture) in place; the intermediate is only created for staging sources. Same A/B, grain
   on / off interleaved ×2: buffering 16/2.2 s and 17/1.6 s vs 17/1.3 s and 15/1.4 s; process CPU at
   80–100 s 5.90/5.99 vs 5.89/5.99 cores, dav1d workers 5.51/5.59 vs 5.53/5.63; 0 late frames.
   GPU grain now costs no measurable CPU, which confirms the copy was the cost. Fusing grain into
   the D3D12 repack is no longer needed for CPU.
   **Shared pool textures accepted (0.1.391, wrapper bale.16):** debug `ffmpeg-tune upload=6` makes
   the pooled MF textures RENDER_TARGET|SHADER_RESOURCE|UNORDERED_ACCESS with
   `MISC_SHARED_NTHANDLE|MISC_SHARED` (log: `bind=0xA8 misc=0x802`). Brawl 4K with zero-copy and GPU
   grain played normally: no E_INVALIDARG, `landing_copies=0`, 0 late frames, buffering 14/1.6 s
   (one run, idle state), correct picture, and `Sample memory: GPU texture (29/29 checked)` with
   `frame-delivery managed`. So D3D12 can in principle write the pool texture directly and drop the
   landing→pool copy (one 4K read + write per frame, like the grain copy above). Not built.
   **Production default (2026-11-09):** `SoftwareDecodeTuning.ZeroCopy` sets `bale_zero_copy=1` for
   every software video route after `ZeroCopyRuntime.Register()` succeeds; `ffmpeg-tune zerocopy=off`
   keeps the swscale path for A/Bs. 12-bit pictures are still declined by the wrapper and take
   today's path until the old paths are removed; 8-bit (`AV_PIX_FMT_YUV420P`) frames go through
   `bale_zc_repack_into_bits` once the wrapper calls it (item 4).
   **Zero-copy only (wrapper 0020, `2.1.0-bale.20`):** "today's path" is gone. `zerocopy=off` and the
   swscale/staging fallback were removed; 12-bit and any other layout zero-copy refuses are server
   transcoded, and every zero-copy failure fails the stream to a server transcode.
A one-off picture freeze (~2 s at start) seen once by the owner during an earlier repack run did not
reproduce in 5 further starts (2 resumes with screenshots, 3 Start Over).

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

**Release in the fork / integrate in Bale:**

1. Commit wrapper source changes to `Zorro92/bale-ffmpeginteropx` and follow its
   [`bale/README.md`](https://github.com/Zorro92/bale-ffmpeginteropx/blob/main/bale/README.md)
   for the `bale-wrapper.yml` build/release procedure. Choose a fresh `2.1.0-bale.N` version.
   The fork builds the checked-out source directly, with no patch application. Its publish guard
   rejects tags already in either repository; never replace an asset or reuse a version.
2. Review the release nupkg, SHA256, `SOURCES.txt`, `winmd-public-types.txt`, build log and corresponding
   source archive. Verify asset hashes, embedded provenance and the source commit, retained original
   package entries, and the built/original public type-name comparison. Obtain the NuGet content hash
   with `PackageArchiveReader.GetContentHash`; do not infer it from the archive SHA512.
3. Update `scripts/ffmpeginteropx-wrapper.json` (`release_repository`, `source_commit`, `version`,
   `release_tag`, `sha256`, `nuget_content_hash`), the exact `FFmpegInteropX.UWP.Lib` reference in
   `src/Bale.Xbox/Bale.Xbox.csproj` and **both** wrapper entries in `src/Bale.Xbox/packages.lock.json`
   together. Update the reviewed assertions in `NativeFfmpegPackageTests.cs` and
   `DebugFfmpegPlaytestTests.cs`, the current release paragraph above, `AGENTS.md` and
   `THIRD_PARTY_NOTICES.md`. Native FFmpeg pins and its build pipeline remain independent in Bale.
4. Run Core tests and the PowerShell fixtures, then commit/push the verified pins and wait for app CI
   to pass, including the Windows MSIX's wrapper/provenance comparisons, activation, dav1d, licenses
   and GPU export checks. The same checks run for production packages when that configuration is
   built. A build alone does not verify console playback behavior.

This is a pinned, repeatable build recipe, not a claim of bit-identical compiler output across
hosted runner revisions. Wrapper patch-application, source-guard, WinMD and repack fixtures belong
to the fork now. Bale's `test-ffmpeginteropx-wrapper.ps1` validates consumer pins and tests release
repository selection, SHA256 rejection, verified-feed population and extraction-cache eviction
with offline restore fixtures. `test-ffmpeg-checks.ps1` retains PowerShell syntax, dav1d binary-version
and MSIX fixture checks, including mismatched wrapper DLL/provenance and unreviewed-package rejection.
Windows CI runs both before downloading the packages; `check-ffmpeg-package.ps1` checks the actual MSIX.

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

#### HDR frame-server probe (phase 0 of GPU tone mapping)

Production HDR on an SDR display stays server tone-mapped (see "Decision: keep server tone mapping"
below). This debug-only probe asks whether a Bale GPU tone-map pass (PQ → linear, BT.2020 → BT.709,
BT.2390 curve) is feasible at all: can the frame server hand Bale hardware-decoded HEVC HDR10 frames
with the HDR values intact, and what does the copy cost at 4K? If it can, Bale could send the server's
`client_managed_dynamic_range_v1` original_http claim, which Apple's clients already send, and map the
picture itself. The probe only measures. It renders nothing and changes no production behaviour.

```
scripts/bale-debug.sh playtest-engine <url|@file|suite> <seconds> rust target=bgra|fp16|rgb10
```

`target` is only for `rust` mode and needs the seconds and the mode before it; `cs` and `rust-ffmpeg`
reject it. `bgra` (the default) is the old behaviour. `fp16` is `DXGI_FORMAT_R16G16B16A16_FLOAT` and
`rgb10` is `DXGI_FORMAT_R10G10B10A2_UNORM`. The format is requested in the two documented places only:
`MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT` on the engine attributes and the format of the destination
texture given to `TransferVideoFrame`. The report prints `Format support` (`CheckFormatSupport`), the
`Format used` read back from the texture and the attribute, and, when a step refuses the format, the
step name and its HRESULT (`SetUINT32 output format`, engine creation, `CreateTexture2D`, or the
count and first HRESULT of failed `TransferVideoFrame` calls).

For `fp16` and `rgb10`, the first frame, then about one frame a second, and the last frame are read
back: a centred crop (1024x1024, or the frame size on an axis where the frame is smaller) is copied to a
staging texture and scanned. The `Readback:` line times that separately (copy+map, which also waits for
the GPU to finish the frame transfer, and the scan); none of it is inside the `TransferVideoFrame`
timing. Compare the transfer times and `cores` of a `target=fp16` run with a `target=bgra` run of the
same clip for the cost of the wider format.

The last line of the readback block is the verdict, taken over all samples so a black last frame cannot
hide HDR:

- `hdr-range=preserved`: the values above SDR white survived. For `fp16` the texture is read as scRGB
  (1.0 = 80 nits) and the peak is above 1.5 (120 nits). For `rgb10` the code values look like PQ: the
  median of max(R,G,B) is below code 520 (about 100 nits in PQ), the 99th percentile is above it, and
  under 0.1 % of pixels sit at or above code 940. This is a heuristic. SDR video puts its median well
  above 520 or piles pixels up at white (940 limited range, 1023 full range), while PQ keeps most of
  the picture low and only highlights high. A very dark SDR clip or a very bright HDR clip can fool
  it, so read the percentiles on the same line.
- `hdr-range=clipped/sdr`: Media Foundation tone-mapped or clipped to SDR before the copy. The
  frame-server route cannot feed a tone-map pass in that format.
- `hdr-range=unknown`: the probe stopped before any readback; the step and HRESULT are above it.

Results (0.1.446, Xbox Series X, 1080p SDR display, Jellyfin 4K HEVC HDR10 40 Mbps clip, 2026-11-10):

- `fp16`: refused. The format is supported for textures, render targets and video processor output,
  and the engine is created, but the open fails after about 0.9 s with `MF_E_UNSUPPORTED_BYTESTREAM_TYPE`
  (`0xC00D36B4`). The same clip opens in `bgra` and `rgb10`, so it is the output format that's refused.
- `rgb10`: plays (865 frames over 15 s, none dropped; `TransferVideoFrame` mean 1.8 ms, max 33 ms;
  0.32 cores, against 2.3 ms and 0.25 cores for `bgra`), but the values are SDR. All 16 samples
  together: max(R,G,B) p50 = p99 = max = code 940, 77 % of pixels at 940, channel max R 940 G 819 B 940.
  The same crop decoded from the source with FFmpeg (limited-range PQ RGB) has max(R,G,B) p50 585
  (about 230 nits), p99 592, max 642 (about 430 nits), and no pixel at 940. So the engine converts
  PQ to SDR and clips everything above SDR white to 940 (limited-range white) before the copy.
  Verdict `hdr-range=clipped/sdr` is right.

Conclusion: the IMFMediaEngine frame server can't feed a Bale tone-map pass. The documented controls
(output format and texture) give no way to get PQ or linear values out. The route that could:
the console exposes D3D11 and D3D12 hardware decode profiles `HEVC_VLD_MAIN10` (P010, up to 8K)
and `VP9_VLD_10BIT_PROFILE2` (P010, 4K) in app mode (`caps gpu`). An FFmpeg hwaccel decode
(d3d11va/d3d12va) into P010 textures Bale owns would keep the PQ values untouched, like the zero-copy
AV1 path. Bale's FFmpeg build has no hwaccel today, so that would be a new native build, not a
probe. Parked.

## Display HDR

**Debug GPU tone-map pass:** `bale_gpu.dll` now provides tone-map ABI 1 (PQ P010 → limited
BT.709 NV12), with BT.2390/Hable and defaults matching the server's peak/white/desat conventions.
Create runs a fixed-pattern CPU/GPU self-test; mismatch returns `0x8004BA01`. The debug switch
`ffmpeg-tune decoder=auto tonemap=off|bt2390|hable|default` requires wrapper 2.1.0-bale.25+;
the pinned .25 wrapper enables it. Older packages reject unknown `bale_*` keys and remain gated.
Production HDR policy remains server tone mapping. See [GPU tone-map ABI and colour details](gpu-tone-map.md),
including the reusable, binding-free `shaders/tonemap_math.hlsli`, 203-nit internal reference,
Hable's explicit desat=0, and documented resampling/quantization differences.

Display HDR is detected at runtime on the UI thread at each player start, not inferred from the
developer's SDR display or installed codecs. `DisplayHdrOutput` reads the supported HDMI modes;
`DisplayHdrPolicy` claims HDR10 if any non-stereo mode supports SMPTE ST 2084 (PQ). ST 2086
metadata support is recorded but is not required for that claim. A successful read reports
`output.display.hdr_evidence = "exact"`, including an empty claim for a non-HDR display. A failed
or unavailable read reports `"unknown"` with empty HDR, so the server fails closed. Both production
and the debug FFmpeg `policy|off` paths use the detected display. `caps` shows the claim/evidence.
HDR10+, HLG and Dolby Vision remain unclaimed/unverified; Xbox Dolby Vision is low-latency DV
(profile 5) only, to revisit later.

**Dolby Vision profile 8 base layer (`client_dv8_base_layer_fallback_v1`, 2026-11-14).** Bale's
original_http delivery carries this claim (as silo-android does). silo-server (protocol_v3.go,
capabilities_v3.go `clientDV8BaseLayerFallbackV3`, candidate_routes_v3.go) then plays a single-layer
DV profile 8 file unchanged through the ordinary HEVC decoder as its base layer, but only with a
proven base layer whose `dv_bl_compat_id` maps to HDR10 (1), SDR (2) or HLG (4), that range on the
active output, and an HEVC decode entry that fits. The plan has decision reason
`client_dv8_base_layer`, recipe range = the base range, warning `dolby_vision_base_layer_only`, and
never claims Dolby Vision output. Bale routes such plans through the FFmpeg demuxer (MP4 too, so
Windows' MP4 source never sees dvh1/dvhe entries) to the system HEVC decoder, which skips the RPU NAL
units (type 62, unspecified in HEVC). An 8.1 plan switches HDMI to HDR10 like plain HDR10; Bale
claims no HLG, so 8.4 stays with the server. Debug `ffmpeg off` withdraws the claim, and a failed
mount withdraws it on replan. `bale-cli play-plan --hdr10` plans like `hdr pretend` on a Series X:
a 4K DV profile 8 MKV now returns original_http instead of an HLS remux; without HDR10 the server
still picks the SDR version.

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
`ffmpeg-tune hdr=on|off|default` sets FFmpegInteropX HDR support for the next real FFmpeg attempt;
`default` restores Automatic, and omitted keys keep their current overrides. Stats and `where` show
`Video media type (track)` on native, FFmpeg and HLS, plus `(mss)` with decoder mode and compressed
status on FFmpeg; both log the MF colour/HDR attributes, with absent attributes shown as `unset`.
`ffmpeg-tune decoder=auto|system|software|default` is debug-only, for the next real FFmpeg route;
use `ffmpeg direct` to force that route for other containers (HLS stays native).
Auto tries FFmpeg d3d11va, reports a software fallback explicitly, and refreshes the MSS decoder/subtype after start.
Production keeps ForceSystemDecoder/ForceFFmpegSoftwareDecoder; `decoder=default` restores that route choice.

For the same-frame comparison, `scripts/bale-debug.sh seek <seconds> [pause]` seeks the active
native, FFmpeg or HLS player to absolute **source/movie** time, optionally pauses, waits for seek
completion and prints actual source/player positions. HLS timeline offsets are mapped rather
than treating a reanchored player clock as movie time. `scripts/bale-debug.sh play`, `pause` and
`toggle` control that same mounted native/FFmpeg/HLS player and print its source/player positions
and resulting state; use `play` to resume after `seek <seconds> pause`. They reject unmounted or
still-opening players and share display/subtitle state updates and sequenced progress reporting.
Non-seekable/loading players, positions outside the current timeline/duration and seeks that do not complete within 10 seconds fail
explicitly. These endpoints and the player hooks compile only under `DEBUG_SERVER`; production
builds cannot use these controls or overrides.

**Debug HLS reproduction:** `scripts/bale-debug.sh delivery hls` disables non-HLS deliveries on
subsequent attempts without changing start time, selected tracks, codec/HDR evidence or the HLS
claims. `delivery auto` restores normal planning; no argument reads the mode. The switch is
memory-only, captured per attempt, and disappears with `BaleDebugServer=false`. CI's development
MSIX uses `Configuration=Release` **with** `DEBUG_SERVER`; the shipping configuration omits it.
The forced attempt logs `player: delivery forced=hls`; the accepted plan still logs the actual
`delivery=server_remux_hls` or `delivery=server_transcode_hls`. Copyable H.264/HEVC SDR with
supported audio and subtitles Off is a useful remux candidate. Unsupported video/HDR, burn-in
subtitles or quality/bandwidth limits may require video transcoding instead; audio-only
conversion can still use `server_remux_hls`.

To check skips: enable the switch, open Detail for an item with saved progress, choose **Start
over**, let it play about 14 seconds, then RT twice and LT three times (or keyboard L, L, J, J, J).
Expect roughly 14 → 44 → 74 → 64 → 54 → 44 seconds, allowing playback between presses. Repeat
with Resume, then leave playback and reset `delivery auto`. No special media or server setting
is required to force HLS; the server still needs an executable HLS route.

The start log records request/source/player/offset seconds; Start over must send explicit zero
regardless of delivery. An omitted start position asks the server to restore saved progress.
Copy-remux plans use the preceding keyframe as `timeline_offset_seconds`, a player start of
`source_start_seconds - offset`, and an open source-time seek window beginning at that origin
(`configureCopyRemuxTimelineV3` in silo-server). Growing encoded HLS uses an aligned segment
origin; complete synthetic VOD uses offset zero and `can_seek_anywhere=true`.
Windows' native HLS seeks can jump to the production edge of a growing EVENT playlist even
inside its advertised seekable range. Neither `NaturalDuration`, played/buffered time nor that
range proves reliable seek landings. Bale therefore reanchors every open-window HLS seek;
only a complete server-published window or `can_seek_anywhere=true` authorizes native HLS seeks.
This uses Android's `PlaybackTimelineSeekPolicy.kt` without its optional mounted-range override;
Apple's `PlaybackTimelineMapper.swift` likewise replans when `can_seek_anywhere=false`.
The replacement mounts once at its returned player start; it must not issue another native
seek after opening, when playback may already have advanced.
Debug seek logs show target, player clock, offset, range count, native/reanchor choice and landed
source/player clocks. The Linux regression harness runs the actual `PlayerPage.Seek.cs`, source
mapping, authenticated start/replan, media transport and progress reporter, with a native-session
stand-in; it does not emulate AdaptiveMediaSource's parsing or Xbox's native HLS landing.

Reanchor requests count mounted plans starting at **1**, as Android's `newActiveAttempt` does;
they retain that count across seeks (2 after Bale's one failure recovery). Zero counts failed
v2's `PlaybackReplanBody` validation before reaching the handler. Seeks keep the same plan,
session, selected tracks and recipe, and do not consume the failure-recovery budget.
`PlaybackProblemLog` emits HTTP status, the recognized problem type's code/title and owning
validation field names/codes. Detail, instance, unknown metadata and dynamic field keys are
never printed.

The CLI can exercise the server contract without a console:
`dotnet run --project tools/Bale.Cli -- play-plan <content-id> --delivery hls --start 0 --reanchor 900 --reanchor 890 --inspect-hls`.
It uses only the test profile and stops the session on exit. Repeat with a nonzero `--start` to
check resume timelines. Inspection prints playlist shape and produced duration, never manifest
URIs. The server normally serves copied video as real variable-duration fMP4 EVENT segments
with a positive `EXT-X-START`, adding `ENDLIST` on completion; encoded video can use full synthetic
VOD. The server's optional keyframe-playlist setting can provide copy VOD from a verified index
when starting at zero, but it is not a client capability and cannot fix resumed growing remuxes.

**Decision: keep server tone mapping.** The owner's console comparison on **0.1.237** used
*Avatar Aang* (1080p HEVC Main10 HDR10+) at **600 / 1800 / 3000 / 4500 seconds**. The media
pipeline's automatic HDR-to-SDR conversion (`hdr pretend`) looked washed out and desaturated,
with lifted blacks; the server's BT.2390/Hable transcode retained color and contrast. Keep
`hdr pretend` as a debug comparison tool, not the normal SDR playback path.

**Why it looked washed out (0.1.463, 2026-11-13).** It is not missing colour info from Bale. Same
paused frame of a plain HDR10 1080p HEVC MKV, SDR display, Device Portal screenshots, luma on 0–255
(active picture), mean HSV saturation:

| Case | Media type MF received | min/p1/p50/p99/max | sat |
|---|---|---|---|
| `hdr pretend`, FFmpeg route, HdrSupport Automatic | no colour attributes (compressed to system decoder) | 0/6/69/128/154 | 0.40 |
| `hdr pretend`, FFmpeg route, `ffmpeg-tune hdr=on` | same, no colour attributes | identical (0 pixels differ) | 0.40 |
| `hdr pretend`, native route (`ffmpeg off`) | PQ, BT.2020, limited, MaxCLL/MaxFALL, mastering, custom primaries | identical (0 pixels differ) | 0.40 |
| server tone-mapped transcode (`hdr off`) | BT.709 SDR | 0/4/61/177/223 | 0.60 |

An HDR10+ HEVC clip on the FFmpeg route repeats it (0/13/43/115/123, sat 0.44 vs server 0/9/32/149/166, 0.61).
All console cases match "no conversion": PQ code values shown as SDR gamma and BT.2020 not mapped to
BT.709, so highlights never pass ~60 % white, mid-tones sit high and colour is flat. Full HDR attributes
on the native route change nothing. `HdrSupport` cannot help either: FFmpegInteropX sets colour
attributes only for video it decodes itself, never for compressed HEVC/VP9/VC-1/MPEG-2 sent to the
system decoder. The 0.1.237 comparison used the native route (HEVC + E-AC-3 had no FFmpeg reason then).
The decoder's output type is not observable from the app. Server tone mapping stays; an on-console
tone map needs Bale's own GPU pass (e.g. d3d11va P010 frames), not a pipeline setting.

**FFmpeg d3d11va on the console (0.1.465, 2026-11-14, debug `ffmpeg-tune decoder=auto`).** With the
fork's wrapper 2.1.0-bale.23 and the existing native FFmpeg (d3d11va already compiled in), the plain
HDR10 1080p HEVC Main10 MKV decodes through FFmpeg d3d11va: `decoder=ffmpeg-d3d11`, output P010,
hardware status Available, no software fallback. Both decoders play in real time (source advanced
15.09 s in 15.09 s wall). Idle process CPU over 10 s windows: d3d11va 0.15–0.18 cores, system decoder
0.13–0.15 cores (both with FFmpeg software DTS audio); app memory 663–686 MiB vs 590 MiB.
Picture at the same paused frame (595.25 s, `hdr pretend`, SDR display): `hdr=default` 0/6/70/128/155
sat 0.40 (media type BT.2020 matrix, transfer/primaries unset); `hdr=on` 0/6/69/128/154 sat 0.40 with
PQ + BT.2020 tagged. Both match the system-decoder result; the server transcode is 0/4/61/177/223
sat 0.60. Even FFmpeg-decoded P010 tagged PQ/BT.2020 gets no conversion from the media pipeline, so a
console tone map needs Bale's own GPU pass on these textures.
Wrapper bug found (also upstream): the HDR side-data reads cast the `AVPacketSideData*` returned by
`av_packet_side_data_get` to `AVContentLightMetadata*` / `AVMasteringDisplayMetadata*` instead of
using its `data`, so `hdr=on` reported MaxCLL 488887616 / MaxFALL 553 (pointer bits; the native
route reads 4057 / 321) and no mastering luminance. The display-matrix rotation read has the same
pattern. The fork fixes all three reads in commit `c3a9adbf2d81024a54063e7570ed9883afd5403c`,
first consumed by Bale as wrapper `2.1.0-bale.24` and retained in `.25`; console verification of that fix remains pending.
Automatic stays banned in production.

**GPU tone map on the console (0.1.475, wrapper 2.1.0-bale.25, 2026-11-14, debug
`ffmpeg-tune decoder=auto tonemap=bt2390`).** Same plain-HDR10 1080p24 HEVC MKV, `hdr pretend`, SDR
display. The fork's frame stage ran bale_gpu's BT.2390 pass (source peak 1000, white 100, desat 0):
`Bale tonemap active: curve=1 1920x1080`, self-test passed on hardware, MF output type NV12 BT.709
limited with no HDR attributes, real time, no replan. Same paused frame (595.25 s):
GPU tone map 0/4/62/169/218 sat 0.62; server transcode 0/4/61/177/223 sat 0.60; no tone map
0/6/69/128/154 sat 0.40. It matches the server closely; highlights end slightly lower.
GPU cost at 1080p (Device Portal engine utilisation): 3D engine 12.5 % mean with the pass vs 6.2 %
without, about 2.6 ms per frame including the slice copy; these samples were taken within one second,
so treat them as rough (the 4K figure below is sampled properly). Process CPU about 0.19 cores either way. Memory over 5 minutes showed no growth on bale.24 or bale.25.
Follow-up (0.1.480): Hable on the same frame 0/3/56/149/198 sat 0.62 (darker than BT.2390 and the
server, so BT.2390 stays the default). An HDR10+ 1080p HEVC title (base layer, dynamic metadata
ignored) with BT.2390: 0/9/32/147/164 sat 0.62 vs the server's 0/9/32/149/166 sat 0.61. An SDR 10-bit
HEVC MKV with the switch on logs `Bale tonemap: request ignored; not a D3D11 10-bit PQ stream.` and
plays unchanged. 4K is untested: no 4K HDR10 HEVC file in the library takes the FFmpeg route.
Production stays on server tone mapping until the owner decides.
4K (0.1.481, 2026-11-14): a Dolby Vision profile 8.1 HEVC MKV, 3840x2160@24, via the new base-layer
claim with `hdr pretend`. System decoder: decodes cleanly with the RPU NAL units left in, real time,
0.155 cores. `decoder=auto tonemap=bt2390`: active at 3840x2160, real time, 0.13 cores, clean
picture. GPU 3D engine (20 samples one second apart) 32.0 % with the pass vs 12.2 % on d3d11va
without it, about 20 points, i.e. about 8.3 ms of GPU per 4K frame including the slice copy (system
decoder alone: 7.9 %). That fits a 24 fps frame (41.7 ms) comfortably.
4K VP9 (0.1.481): a VP9 profile 2 10-bit HDR10 MKV, 3840x2160@23.976, 28.7 Mbps, `hdr pretend`,
original_http. System decoder: real time, 0.17 cores, but the media type Media Foundation receives has
no colour attributes at all. VP9 carries no colour signalling of its own (unlike HEVC's VUI/SEI), so
on an HDR TV the system VP9 path likely presents PQ as untagged and sends no static metadata; this is
unverified without an HDR display. The fix belongs in the fork: tag compressed video media types
from the container. `decoder=auto tonemap=bt2390`: vp9_d3d11va decodes it, the pass runs at
3840x2160 in real time (0.28 cores, 3D engine 31.7 %), and the picture is clean with full contrast.
App memory for this file is 1.2–1.3 GiB (vs 0.6–0.7 GiB for 1080p HEVC).

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

## Display resolution matching

A title can have several file versions (say a 4K remux and a 1080p encode). With the version
selector on Auto, the official apps pick the highest resolution whatever the TV
(`PlaybackVersionSelector.kt`, `DetailVersionSelection.swift`). **Bale deviates on purpose:** Auto
picks the version that best matches the connected display.

The rule, in `PlaybackFormatting.SelectVersion` / `MatchDisplay` (Core):

1. An explicit version pick wins, then the last played file, as in the official apps.
2. Otherwise, on Auto, the display's height is the height of its largest supported HDMI mode:
   - the best version at or below that height that a decode claim admits (so it plays as it is);
   - else the best version at or below that height (the server transcodes it);
   - else the smallest version above it, preferring one a decode claim admits.
   "Best" keeps the official order: resolution, then bitrate, then file size.
3. A 4K file is still picked when the display is 4K and the decode claims admit it, and when it
   is the title's only version.
4. Quality "original" / "source" / "max" and the resolution caps ("720p", ...) are untouched.
5. If the display modes can't be read, Auto is the official highest-first.

Why: on a 1080p TV the 4K version costs several times the bandwidth, and on this console often a
server transcode as well, for a picture the TV can't show. The 1080p version of the same title
plays directly. It is on by default with no switch yet; the toggle arrives with the 0.2.x Settings
menu, so a cinephile who wants the highest-quality file every time can turn it off. Off leaves
`DisplayVersionMatch.Current` null, which is exactly the official behaviour.

**Codec claims stay decoder-based.** The capabilities sent to the server say what the console can
decode and never shrink to the display; the display's size lives only in this version choice. The
choice reads the same production decode claims the player sends (`PlaybackClaims`), so the Detail
readout names the file the player will ask for. The hardware claims follow the decoder probe, so
on a Series console a 4K TV gets a title's 4K HEVC version and a 1080p TV its 1080p one. The
debug-only claim overrides stay in `PlayerPage` and are not seen by the matching.

`PlaybackClaims.RefreshVersionMatch` sets the match on the UI thread at launch, once the window
is active, and again each time a Detail page opens, since the TV can change. The launch call also
starts the decoder probe on a pool thread; the match is set when it finishes. The safe log is
`version match: display=<height>p claims=<count>` or `version match: off (no display modes)`,
written when it changes. This is separate from phase 6's display *mode* matching below, which
switches the HDMI mode to fit the content and logs `display match:`.

Core tests cover the rule and the claim bounds; the Xbox read has tests with an HDMI stand-in.
**Not yet verified on the console.**

### What the server decided

Bale's choice is only the file it asks for. The server can answer with another version of the same
edition: when the requested 4K file would need a transcode, its media-version fallback
(`playback.prefer_lower_version_transcode`, on by default; silo-server
`docs/architecture/playback-protocol-v3.md`) serves the lower qualities from a lower-resolution
version. A 4K Dolby Vision pick on an SDR display then plays the title's 1080p H.264 file as it
is. The plan says so in `requested_media_file_id` / `effective_media_file_id`, and two diagnostics
show it:

- **Log**, one line for every plan the player mounts (the start, the failure replan and each seek
  reanchor), from `PlaybackPlanLog.Describe` in Core:
  `player: plan delivery=original_http reason=quality_device_limit requested=<id> effective=<id>
  quality=1080p warnings=<code>,<code> transformations=<name>,<name>`.
  Empty fields are left out. Every value is a protocol id held to `PlaybackStats.MetadataId`
  (ASCII letters, digits and `_-.+ `, at most 160 characters); anything else is written as `?`.
  Warnings give their code, never the message; transformations their name. A list shows eight
  entries, then `+N`. The stream URL, grants, headers and source details are never written.
- **Stats**, in the **Delivery** row after the delivery and its `decision_reason` text
  (`PlaybackStats.DescribeDecisionReason`): `Direct play · 1080P version instead of 4K · DV`.
  The names are the Detail readout's (`PlaybackFormatting.VersionCompactLabel`) for the two
  files in the title's versions. Two versions with the same label read `other 1080P version
  played`; a file that isn't in the versions, or has no usable label, reads `other version
  played`. No note when the two ids match or the plan names no requested file.

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

## PGS sidecars on packaged playback

`PlaybackSubtitleResolver` accepts the server's `render`/`.sup` artifact. Original HTTP continues
using the FFmpegInteropX stream-index sink (wrapper 0022, unchanged). HLS and progressive advertise
`embedded_bitmap`; silo-server `subtitle_policy_v3.go` gates that boolean to embedded PGS. Neither
external bitmap sidecars nor DVD/DVB extraction is claimed. `sidecar_bitmap` stays false. This
removes a PGS-only burn-in requirement from an otherwise remuxable route; video or audio conversion
required for other reasons is still the server's decision.

`PgsSubtitleRange` requests `windowed=1&position=…&duration=180`, starting 30 source seconds before
the current position, and refreshes 45 seconds before coverage ends. The server defaults to a full
track without that opt-in. Its positioned extracts retain source PTS (`-copyts` with container-start
correction); the client must fetch again outside coverage. The cache can warm a full extracted
track in the background, then slice future windows from it. Bale limits each binary response to
16 MiB and each decoded window to 64 MiB of indices/palettes and 4096 events, and serializes native
decodes. An overlapping window may carry one still-visible picture if extraction starts inside
a long display set; retained indices stay bounded by the new window plus that one picture.
Parsing/fetching never runs on the UI thread. A failed window logs a fixed stage/HRESULT
and retries after 10 seconds without changing the playback plan.

Rust `subs/pgs.rs` handles PCS/WDS/PDS/fragmented ODS/END, PAL8 RLE, limited-range BT.709 (SD:
BT.601), palette updates, forced flags, cropping and clears. Synthetic fixtures contain only small
hand-authored segment structures. The bitmap ABI remains version 1; subtitle overlay ABI is 3.
A decoded handle is committed atomically only after mount and request checks. Retained windows
survive backward seeks; leaving coverage clears the previous picture while fetching. Source time
comes from the mounted `PlaybackTimeline` via `SourcePosition`, so resume, Start over and reanchor
origins are applied once. Paused seeks refresh too. Remounts cancel old requests, unbind with token
0 and fetch/rebind under a fresh token. A window's final cue cannot outlive its coverage.

Console validation (owner-run; implementation deliberately performs no console actions):

1. Set `scripts/bale-debug.sh delivery hls`. Select an embedded English PGS track on a source whose
   video/audio HLS route can remux. Expect `server_remux_hls`, subtitle `mode=render` with a `.sup`
   artifact, and `player: subtitles mode=render format=sup windowed=1` in the log. No subtitle-only
   video transcode. `scripts/bale-debug.sh suboverlay stats` should show queued events and images
   during an authored cue.
2. Watch past three minutes (a window rollover). Use `key l` / `key j` for forward/backward skips;
   use `seek <source-seconds>` to cross both window edges and the HLS origin. Check cue starts,
   clears, placement in letterbox bars, and no old picture after a seek. Pause and seek both ways;
   cues should reflect the landed source time and remain still until playback resumes.
3. Exit, Resume at a later source time, then exit and choose Start over. Confirm the opening cues
   return at their original times. Reanchor before the remux origin and repeat backward/forward.
4. Repeat with a route requiring video transcode for a separate codec/quality reason. It should
   retain `.sup`/render instead of adding PGS burn-in. Repeat with forced PGS selected through Auto
   (`show forced` and matching audio language), explicit PGS, and Off; check no stale overlay.
5. Restore `scripts/bale-debug.sh delivery auto` and verify direct MKV still uses the native
   embedded stream-index bitmap path. Progressive remains disabled by production transport policy;
   its subtitle claim is ready when that transport is enabled, and is covered by unit/server tests.
