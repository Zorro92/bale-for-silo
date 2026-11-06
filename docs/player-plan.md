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
scripts/bale-debug.sh ffmpeg-tune [readahead=on|off|default] [readsize=N|default] [prefetch=N|default]
```

No arguments reads the current process-wide overrides (`GET /ffmpeg-tune`); arguments update them
atomically (`POST /ffmpeg-tune` with the same query keys). Keys may be supplied in any order, once
each. Omitted keys keep their previous overrides; `default` clears only that key. Restarting the
process clears all overrides. Settings are captured at playback-attempt creation, retained through
recovery/remounts, and applied only to the next real FFmpeg Direct player attempt, not an already
open player, Native Direct, HLS or the standalone probe. `ffmpeg policy|off` remains independent.

- `readahead=on` enables FFmpegInteropX's native demux/packet read-ahead queue with **64 MiB /
  30 s** caps, rather than its larger 100 MB / 60 s defaults, to bound the extra Xbox memory use.
  These are the wrapper's queue thresholds, not a total-process memory ceiling. `off` explicitly
  disables it; `default` does not set any read-ahead property (native default: disabled).
- `readsize=N` sets `General.FileStreamReadSize`, in **bytes**, inclusively 16384–1048576.
  `default` leaves the native 16384-byte default alone.
- `prefetch=N` sets the authenticated `HttpRangeStream` depth, inclusively **2–16** aligned 1 MiB
  blocks. The default stays **2**. This immutable per-stream Core hook does not change the 32-block
  cache or the three-concurrent-fetch limit; deeper speculation queues behind that limit.
- Production (`BaleDebugServer=false`) never applies the experiment and keeps all original defaults.

For an isolated comparison, close the current player, set the baseline, then open the same title:

```sh
scripts/bale-debug.sh ffmpeg-tune readahead=default readsize=default prefetch=default
# After closing the baseline player, change only native read-ahead and reopen:
scripts/bale-debug.sh ffmpeg-tune readahead=on
# Optional, separate experiments (close/reopen after each change):
scripts/bale-debug.sh ffmpeg-tune readsize=65536
scripts/bale-debug.sh ffmpeg-tune prefetch=8
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

The standalone `playtest-ffmpeg` URI probe is for public or independently fetchable media. A private
`play-plan --url-file` file protects the URL on disk, but does not convey Bale's profile/authentication
headers to FFmpeg. Use the real-player toggle for library files, under **Bale Test**.

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

Bale uses `FFmpegInteropX.UWP.Lib` **2.1.0** with the custom x64 native package
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
   `NuGet.Config` maps only this package ID to that feed; the project requires its exact version.
   The downloader clears this package's cached extraction in CI so the verified asset is used.
   For a local Windows build, run `pwsh -File scripts/restore-ffmpeg-native.ps1` first.
5. Update the project reference and both native entries in `src/Bale.Xbox/packages.lock.json`
   together with a reviewed package/hash change. Keep wrapper 2.1.0 unless a real ABI dependency
   requires otherwise. Run Core tests and watch debug **and** production MSIX CI. The package checker
   reads the actual MSIX `avcodec-62.dll` and rejects anything without dav1d **1.5.4**; update its
   expected version/tests deliberately for a later dav1d upgrade. Licenses and provenance ship in
   both configurations.

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
legacy unknown-range sources switches the same way. General title refresh-rate matching is still
future work. HDR10 direct play switches HDMI to HDR and restores the default afterwards: Back,
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
- HDR10 output switching and restore are implemented above, pending real HDR hardware checks.
- Later: match HDMI refresh rate to the title during playback and restore the default on exit
  (`HdrDisplayController.kt` / `RefreshRateMatcher.kt`); the current HDR switch preserves the
  current display's resolution/refresh where possible.

### Later
- Shuffle, intro/credits skip (`TvIntroAutoSkipBanner.kt`), trickplay thumbnails.
- Subtitle search / styling.
- Watch Party.
- Audiobooks.

## Testing rules
- Every server write (progress, watched, track picks) runs under the **Bale Test** profile.
- Drive the player with `bale-debug.sh key/press` and check it with `where`, `wait` and
  screenshots.
