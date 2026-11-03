# Video player plan

Bale has no player yet. This is the phased plan for one. Like everything else, it copies the
official Android TV player (`silo-android/androidTvApp/.../tv/ui/screens/player/`, protocol in
`shared/.../model/playback/PlaybackProtocolV3.kt`, `network/apiv2/PlaybackV2Api.kt`,
`repository/SequencedPlayback.kt`). The tvOS docs under `silo-apple/docs/tvos-player/` are marked
historical; use them only for intent.

## Engine

The engine is Windows' built-in `MediaPlayer` + `MediaPlayerElement`: hardware decode, HDR output and
Dolby/DTS passthrough on the console. It only uses decoders registered with Windows.

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
| MP4 / WebM AV1 (8-bit and 10-bit) | **fails**: `UnsupportedSubtype`, MediaFailed 0xC00D5212 (no decoder). Definitive: MediaPlayer can't play AV1 here. |
| WebM VP8+Vorbis, VP9, VP9+Opus | opens, decoders report FullySupported, but never leaves Paused at 0 s. Unexplained; WebM is rare in libraries, so don't claim it. |
| MP4 FLAC, MP4 Opus | `UnsupportedSubtype`: MP4's `fLaC`/`Opus` sample entries aren't mapped, though the FLAC/Opus decoders exist. Retest inside MKV with real files. |

Still to test with real library files: MKV, HEVC Main10, TrueHD/DTS passthrough to a receiver.

The capability claims Bale sends the server must come from this kind of evidence, never from
guesses. `playtest` (debug only) checks actual playback, not just installed decoders.

**Open decision: a bundled decoder (FFmpegInteropX).** It would add software AV1 (dav1d), DTS/TrueHD
decode without a receiver, broader MKV and ASS styling. The costs are package size, an untested
Native AOT fit and a second pipeline. Not in the first phases. Until then, whatever the console
can't play goes to the server to transcode. Edge plays AV1 on this console through Chromium's own
decoders, which doesn't prove MediaPlayer can.

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
