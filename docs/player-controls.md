# Player controls: official TV player vs Bale

Status 2026-11-16, for planning the next player phase (docs/player-plan.md phases 3–5). Sources:
silo-android `androidTvApp/.../tv/ui/screens/player/` (port of the tvOS player).

## What Bale has today

- Triggers (and keyboard J/L): skip back / forward, one skip per press, with the skip chip
  (delta, landing time, thin progress line, elapsed / remaining / "Ends hh:mm"), hidden after 1.2 s.
- Back leaves the player (stop + final position).
- Debug: View toggles the stats overlay; `bale-debug.sh play|pause|toggle|seek` drive the player.
- Nothing else on screen: no transport overlay, no play/pause from the controller, no track
  switching during playback, no chapters, no Up Next.

## What the official TV player has

| Area | Official behaviour | Source | Bale |
|---|---|---|---|
| Play/Pause | Media key / Space act on the first press, repeats and key-up swallowed | `TvPlayerRemoteKeyAction.kt` | missing |
| Stop key | Ends playback and leaves the player | same | missing |
| Clean-playback Left/Right | Skip back / forward (profile intervals, 10 s / 30 s on older servers) with the skip chip | `TvSkipSeekIndicator.kt` | triggers only |
| Hold Left/Right | Hold-to-seek on a rate ladder 1×–32× (100 ms tick, 2 s per tick at 1×, ramp 2/4/8 every 1.2 s), hold chip with rate and landing bar | `TvSeekRateLadder.kt`, `TvHoldSeekIndicator.kt` | missing |
| Transport overlay | Down / any unmapped key reveals it; auto-hides after 5 s of no input | `TvPlayerScreen.kt` | missing |
| Scrubber | Glass capsule; focus = scrub mode; tap Left/Right nudges ±10 s, hold auto-seeks, Select commits, Back/Down cancels; chapter ticks and segment colours; time bubble | `TvPlayerScrubber.kt` | missing |
| Transport row | Left: skip back · play/pause · skip forward. Right: Up Next · Subtitles · Options · Close. Discs that widen into labelled pills on focus ("Subtitles · English") | `TvPlayerTransportCluster.kt` | missing |
| HUD | Top-centre card, tabs Info · Stats · Video · Audio · Subtitles · Chapters (empty ones hidden); row → picker model; Menu opens it on Video, Down from clean playback on Audio/Subtitles | `TvPlayerHud.kt` | debug stats only |
| Audio / subtitle switch | Local track switch, or a server `replan` when the route needs it | `TvPlayerViewModel.kt` | missing (Detail page only) |
| Subtitle delay / appearance | In the Subtitles tab | `TvPlayerHud.kt`, `TvSubtitleAppearanceOptions.kt` | missing |
| Chapters | Tab lists chapters, Select seeks and closes | `TvPlayerHud.kt` | missing |
| Intro / credits skip | "Skip Intro" pill (ask) or "Intro skipped / Watch Intro" (always), fill tracks the timer | `TvIntroAutoSkipBanner.kt` | missing (player-plan "Later") |
| Up Next | Next-episode card with 10 s countdown, On Deck cards, auto-play setting | `TvOnDeckItems.kt`, `NEXT_UP_COUNTDOWN_SECONDS` | missing |
| Trickplay thumbnails | Preview frames on the scrubber (server `WatchTrickplaySheet`) | scrubber | missing |

## Proposed order (each step verified on the console before the next)

1. **Play/Pause and Stop.** Code done 2026-11-16 (`PlaybackRemoteKeys`, `PlayerPage.Controls.cs`):
   media Play/Pause and Space toggle, media Stop leaves, D-pad Left/Right and media
   previous/next skip, all on the first press; the system media controls' Play/Pause/next/previous/
   fast-forward/rewind take the same path, and a press arriving by both routes acts once. The log
   line `player: transport key=… / system=…` shows which route the Xbox media remote uses; check
   that with the remote on the console. Select (A) is left for step 2 (it reveals the controls).
   Verified on 0.1.568 with keyboard Space and the controller D-pad: pause/resume, +30 s and −10 s
   (also while paused, which stays paused), one `transport key=32` line per press, no second
   toggle from the system controls. Still to check with you: the media remote and controller A.
2. **Transport overlay and scrubber.** Reveal on Down/any key, 5 s auto-hide, the transport row
   (skip, play/pause, Close first; Subtitles/Options buttons once step 4 exists), scrubber with
   nudge/commit/cancel and chapter ticks. Left/Right on clean playback become skips, as official.
3. **Hold-to-seek.** The rate ladder and hold chip, on the existing seek path (exact landing with
   FastSeek off, keyframe for software AV1).
4. **HUD: Audio and Subtitles tabs** with mid-playback switching (local switch or replan), then
   Info/Video/Chapters; the debug stats move into the Stats tab.
5. **Up Next** (10 s countdown, auto-play setting); this also brings "the last special continues
   into S1E1" from the upstream log.
6. Later: intro/credits skip, trickplay, subtitle delay/appearance.

