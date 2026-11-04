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
| Display | 720p/1080p at 50/59.94 Hz, SDR only (no HDR10/DV modes reported) |

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

Current production capability claims (`ConsoleEvidence.Default`, max audio channels **8**):

| Stream | Claims / engine | Bounds / limitations |
|---|---|---|
| H.264 / HEVC video | Media Foundation, hardware | 1920×1080, 60 fps, 25 Mbps; H.264 8-bit, HEVC 8/10-bit |
| AV1 video | dav1d, **non-hardware**; `software_video_decode_v1` opt-in | 8/10-bit, **1920×1080, 30 fps**; no 4K claim |
| System audio | `aac`, `ac3`, `eac3`, `mp3`, `pcm` | Retains the native direct path unless AV1 needs FFmpeg |
| FFmpeg fallback audio | `dts`, `dts_hd`, `truehd`, `flac`, `opus`, `vorbis`, `alac` | Decode to PCM, not passthrough; real-library console verification pending |
| Audio passthrough | None | Receiver verification pending |
| HLS | System video/audio claims only | AdaptiveMediaSource cannot use FFmpeg fallbacks |
| Subtitles | None locally | Server burn-in; **TODO:** add FFmpeg subtitle fallback when Bale renders subtitles |

`PlaybackRoutePolicy.Decide` selects the pipeline separately for every mount. `original_http` /
progressive uses FFmpeg only for AV1 or the **selected** fallback audio codec. AV1 sets
`Video.VideoDecoderMode = ForceFFmpegSoftwareDecoder`; audio-only fallback sets
`ForceSystemDecoder`. Never use automatic or FFmpeg D3D11 video in real playback. The pinned
2.1.0 implementation can fall back even in ForceSystemDecoder for unsupported formats, so Bale
also validates all reported video decoder engines before mounting/reading frames.

`Audio.SystemDecoderAAC` and `Audio.SystemDecoderMP3` are enabled. The pinned winmd has **no
system AC3/EAC3/PCM or encoded-passthrough switches**: those use FFmpeg audio on the AV1 path,
while ordinary HEVC/H.264 + AC3/EAC3 remains native. FFmpegInteropX moves its preferred audio
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
- Match HDMI refresh rate (and HDR when the display has it) to the title during playback,
  restoring the default on exit (`RefreshRateMatcher.kt`; jellyfin-xbox `FullScreenManager.cs`
  uses the same `HdmiDisplayInformation` calls).

### Later
- Shuffle, intro/credits skip (`TvIntroAutoSkipBanner.kt`), trickplay thumbnails.
- Subtitle search / styling.
- Watch Party.
- Audiobooks.

## Testing rules
- Every server write (progress, watched, track picks) runs under the **Bale Test** profile.
- Drive the player with `bale-debug.sh key/press` and check it with `where`, `wait` and
  screenshots.
