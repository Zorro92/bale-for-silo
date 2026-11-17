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

## Bale's controller map (deviation, owner 2026-11-16)

See AGENTS.md. In short: D-pad reveals the controls, A pauses (and reveals) or resumes a paused
player whose controls have hidden, triggers tap-skip 10 s / 30 s and on hold rewind / fast-forward
steadily after a short pause, bumpers jump between chapters. Media remote keys keep the official map.
The controls are XAML: the composition engine already draws them on the GPU (see "Rendering").

## Rendering

The controls stay XAML. XAML is composited by the system on the GPU (DirectComposition / D3D11);
the CPU only does layout and the occasional value change. Drawing them in Bale's own D3D11 layer, as
the subtitles are, would mean rebuilding focus, text and accessibility for no saving. Keep the CPU
side small: fade and slide with composition animations (they run off the UI thread), update the
progress bar and times a few times a second rather than per frame, and hide (collapse) the controls
rather than leaving them transparent so they cost nothing while hidden.

## Proposed order (each step verified on the console before the next)

1. **Play/Pause and Stop.** Code done 2026-11-16 (`PlaybackRemoteKeys`, `PlayerPage.Controls.cs`):
   media Play/Pause and Space toggle, media Stop leaves, media previous/next skip, all on the first
   press; then the owner's controller map: A pauses / resumes, triggers skip, bumpers jump between
   chapters, the D-pad is held for revealing the controls (step 2); the system media controls' Play/Pause/next/previous/
   fast-forward/rewind take the same path, and a press arriving by both routes acts once. The log
   line `player: transport key=… / system=…` shows which route the Xbox media remote uses; check
   that with the remote on the console. 
   Verified on 0.1.568 (before the controller map) with Space and D-pad skips: pause/resume, +30 s and −10 s
   (also while paused, which stays paused), one `transport key=32` line per press, no second
   toggle from the system controls. Still to check with you: the media remote and controller A.
2. **Transport overlay and scrubber.** Revealed by the D-pad or by A pausing, hidden after a few
   seconds without input, the transport row (skip, play/pause, Close first; Subtitles/Options once
   step 4 exists), scrubber with nudge/commit/cancel and chapter ticks.
   Verified on 0.1.572 with injected input: D-pad reveals with Play/Pause focused, Up to the bar,
   Right x3 previews +30 s, Select commits, Down back to the buttons, 5 s idle hides, Back hides then
   leaves, triggers +30/-10 s, Close leaves. Injected gamepad buttons arrive as keyboard keys
   (A as Space, D-pad as arrows), so Space is treated as A; a real controller still to check.
   Code: `TransportControls` in PlayerPage.xaml,
   `PlaybackControlsState` / `PlaybackScrub` / `PlaybackControlsText` in Core. Eyebrow and title,
   the scrubber (focused: Left/Right move a 10 s preview, Select commits it as one seek or toggles
   playback without one, Down/Back cancel it), the clock row with "Ends", and skip back · play/pause ·
   skip forward on the left, Close on the right. Play/Pause has focus on reveal; Back closes a
   preview, then the controls, then the player; Stop and Close leave at once; 5 s without input hides
   the controls (also while paused). Chapter starts are 2 px gaps in the bar. Not yet: segment
   colours (intro/credits markers), the skip chip's delta while the controls are up.
3. **Trigger hold.** After a short pause (about 1 s) a held trigger rewinds / fast-forwards at one
   steady rate until released, with a chip showing where it will land; release commits. Code done 2026-11-16
   (`PlaybackHoldScan`): 1 s delay, 60 s of video per second, chip "Rewind"/"Fast-forward" with the
   landing time; the scan ends without seeking if the player leaves. Rate to tune on the console. Built on
   the existing seek path (exact landing with FastSeek off, keyframe for software AV1).
4. **HUD: Audio and Subtitles tabs** with mid-playback switching (local switch or replan), then
   Info/Video/Chapters; the debug stats move into the Stats tab.
   First part, code done 2026-11-16: Audio and Subtitles buttons in the transport row (pills
   "Audio · English", "Subtitles · Off" on focus) open the Detail page's track lists for the playing
   file; a pick sends a `track_change` replan of the same session at the current position
   (`PlaybackReplan.ForTrackChange`) and remounts like a seek reanchor; a refused change keeps
   playback as it was. Always a replan for now; the official local switch (a track the mounted
   stream already carries) is a later optimisation.
   Options panel verified on 0.1.577 (Blu-ray movie with chapters): Menu opens it with Info focused
   (MOVIE · 2016, title, 1:41:31, HDR10/1080p, Direct play · HEVC · DTS 5.1 · English · Chapter 02);
   Chapters lists 01-07 with times, Chapter 04 seeked to 25:55 and closed. Bumpers on the same
   title: RB to Chapter 05 (32:11), LB within 3 s back to Chapter 04.
   Options panel, code done 2026-11-16 (`PlayerPage.Options.cs`, `PlaybackOptionsInfo` in Core): the
   control bar's Options button or the Menu button opens a top-centre card that replaces the controls
   until Back. Info: eyebrow, title, length, HDR/resolution badges, and Stream rows (Playback, Video,
   Audio, Subtitles, Chapter) in names people know. Chapters (only with chapters): a list with start
   times; a pick seeks and closes. Not yet: the Video tab (playback settings), Audio/Subtitles tabs
   (the buttons cover them), moving the debug stats into a Stats tab.
   Verified on 0.1.574 (subtitles): PGS -> SRT replanned `playable` and remounted at the same
   position in about 2 s with the new track; Off replanned and no subtitles load. Audio list not
   exercised (the title had one language). Left/Right along the button row are explicit since.
5. **Up Next** (10 s countdown, auto-play setting); this also brings "the last special continues
   into S1E1" from the upstream log.
   Verified on 0.1.576: started 24 s before the end of S1E1, the card rose at the credits marker
   with Play Now focused, the countdown waited for the end and S1E2 started in a fresh player; Back
   then returned to the series page. Back on the card hid it, and the end left to Detail.
   Code (`PlaybackUpNext` / `PlaybackUpNextState`, tested
   in Core; `PlayerPage.UpNext.cs`): for an episode the next one is resolved once playback starts
   (next number in the season, else the first episode of the next numbered season; the last special
   continues into S1E1, a regular episode never into Specials). Crossing the file's credits marker
   raises the card with a countdown mirroring the remaining playback, so the advance waits for the
   end; without a marker the card appears at the end with a 10 s countdown. Play Now or the countdown
   opens a fresh player for the next episode that replaces this one (Back still returns to Detail);
   Hide/Back dismisses the card for this episode. Auto-play is always on for now; the Settings
   toggle and the transport's Up Next button come later.
6. Later: intro/credits skip, trickplay, subtitle delay/appearance.

