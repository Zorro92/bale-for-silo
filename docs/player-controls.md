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
| HUD | Top-centre card, tabs Info · Stats · Video · Audio · Subtitles · Chapters (empty ones hidden); row → picker model; Menu opens it on Video, Down from clean playback on Audio/Subtitles | `TvPlayerHud.kt` | Options panel, all six tabs (step 4); no Menu-on-Video or Down-to-Audio entry yet |
| Audio / subtitle switch | Local track switch, or a server `replan` when the route needs it | `TvPlayerViewModel.kt` | control-bar menus and Options tabs: local audio switch on direct play, else replan |
| Subtitle delay / appearance | In the Subtitles tab | `TvPlayerHud.kt`, `TvSubtitleAppearanceOptions.kt` | missing |
| Chapters | Tab lists chapters, Select seeks and closes | `TvPlayerHud.kt` | missing |
| Intro / credits skip | "Skip Intro" pill (ask) or "Intro skipped / Watch Intro" (always), fill tracks the timer; untimed "Skip Recap" / "Skip Credits" pill | `TvIntroAutoSkipBanner.kt`, `TvPlayerScreen.kt` | step 6 (2026-11-25), console check pending |
| Up Next | Next-episode card with 10 s countdown, On Deck cards, auto-play setting | `TvOnDeckItems.kt`, `NEXT_UP_COUNTDOWN_SECONDS` | missing |
| Trickplay thumbnails | Preview frames on the scrubber (server `WatchTrickplaySheet`) | scrubber | missing |

## Bale's controller map (deviation, owner 2026-11-16)

See AGENTS.md. In short: D-pad reveals the controls, A pauses (and reveals) or resumes a paused
player whose controls have hidden, triggers tap-skip 10 s / 30 s (on release) and on hold (past ~250 ms)
rewind / fast-forward steadily from where playback is, without skipping first, bumpers jump between chapters. Media remote keys keep the official map.
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
   Subtitle lift (2026-11-16): while the controls are up the subtitle layer slides up 190 so cues clear
   the title, bar and buttons (TvPlayerScreen.kt liftSubtitlesClearOfControls, ~216 dp reach); hidden
   while the Options panel is open. Bale moves the whole layer, so a top-placed cue moves with it.
   Options panel, code done 2026-11-16 (`PlayerPage.Options.cs`, `PlaybackOptionsInfo` in Core): the
   control bar's Options button or the Menu button opens a top-centre card that replaces the controls
   until Back. Info: eyebrow, title, length, HDR/resolution badges, and Stream rows (Playback, Video,
   Audio, Subtitles, Chapter) in names people know. Chapters (only with chapters): a list with start
   times; a pick seeks and closes.
   Stats, Audio and Subtitles tabs, code done 2026-11-17 (`PlaybackOptionsTabs` in Core, `PlayerPage.Options.cs`,
   `PlayerPage.HudStats.cs`): tabs in the TV order Info · Stats · Video · Audio · Subtitles · Chapters, hidden when
   empty (Stats while it has rows, Audio with any audio track, Subtitles with any subtitle track as on tvOS,
   Chapters with chapters). Stats shows TvPlayerHud.kt's hudRows() only (Backend, Route, Hard containers, codecs,
   resolution, frame rate, HDR mode, decoders, Estimated bandwidth, dropped frames / underruns when non-zero) in two
   columns, refreshed once a second while open, in every build (the debug overlay keeps its extra rows).
   Audio and Subtitles list every track (Subtitles with Off first and "Tracks · N"), the playing one checked
   with a soft fill; Select switches through the control bar's path (local audio switch on direct play, else a
   replan) and the panel stays open while the check moves. D-pad: Left/Right walk the tabs and stop at the ends,
   Down from a tab lands on the checked row, Up/Down walk the rows, Up from the first row returns to its tab, the
   last row and Left/Right inside a pane stay put.
   Not mirrored (Bale has nothing behind them yet): Audio delay and Android's Output column (codec / passthrough
   mode / decoder), subtitle delay, timing sync, appearance and size, secondary subtitles, subtitle search and AI
   translate, the pending "…" value while a switch is in flight, greyed-out unsupported subtitle formats, and the
   Video tab's quality / speed / aspect / sleep / intro-skip rows.
   Verified on 0.1.574 (subtitles): PGS -> SRT replanned `playable` and remounted at the same
   position in about 2 s with the new track; Off replanned and no subtitles load. Audio list not
   exercised (the title had one language). Left/Right along the button row are explicit since.
   Track preferences, code done 2026-11-25 (`PlayerTrackPreferences` in Core, `PlayerPage.Tracks.cs`; Android
   `TvPlayerViewModel.kt` rememberSeriesAudioChoice / rememberSeriesSubtitleChoice, Roku `3ae62aa`): a pick in either
   menu, the checked row included, is saved as the series' (an episode) or movie's track preference once a plan plays
   it, with the body a Detail pick writes: on the local audio switch, the in-place subtitle answer, or the opened
   replacement mount (also a later replan that asks for it again). A refused or failed change saves nothing, nor does
   anything the player or server chose itself (start tracks, an Auto burn-in, a fallback); Off is saved as Off, a
   burned-in answer as the picked track. The track is matched through the plan's inventory row (embedded by stream
   index, external by place among the externals when language and codec agree). The write outlives the player; Up Next
   and the Detail page wait up to 1.5 s for it before reading, and Detail puts its own picks of the saved kinds back on
   Auto and reads its choices again (`TrackChoiceReturn`). Console check pending.
5. **Up Next** (10 s countdown, auto-play setting); this also brings "the last special continues
   into S1E1" from the upstream log.
   Verified on 0.1.576: started 24 s before the end of S1E1, the card rose at the credits marker
   with Play Now focused, the countdown waited for the end and S1E2 started in a fresh player; Back
   then returned to the series page. Back on the card hid it, and the end left to Detail.
   Code (`PlaybackUpNext` / `PlaybackUpNextState`, tested
   in Core; `PlayerPage.UpNext.cs`): for an episode the next one is resolved once playback starts
   (next number in the season, else the first episode of the next numbered season; the last special
   continues into S1E1, a regular episode never into Specials). Crossing
   `playback.next_up_prompt_seconds` before the end of the file (default 30, read with auto-play;
   `NextUpPrompt`, Android `e580e1811`) raises the card with a countdown mirroring the remaining
   playback, so the advance waits for the end; with "At end" (0) the card appears at the end with a
   10 s countdown. The credits marker no longer moves the card (it only records the series-page
   return), and the prompt point leaves a hand-opened card without a countdown. Play Now or the countdown
   opens a fresh player for the next episode that replaces this one (Back still returns to Detail);
   Hide/Back dismisses the card for this episode. Auto-play is always on for now; the Settings
   toggle and the transport's Up Next button come later.
6. **Intro and credits skip.** Code done 2026-11-25, console check pending (`PlaybackSkipMarkers`, `IntroSkipPrompt`,
   `MarkerTimeline` in Core; `PlayerPage.SkipMarkers.cs`). Sources: Android `TvPlayerViewModel.kt`, `TvPlayerScreen.kt`
   (TvPlayerOverlays and the root key handler), `TvIntroAutoSkipBanner.kt`, shared `IntroAutoSkipController.kt`,
   `MarkerTimeline.kt`, `MarkerSkipTarget.kt`; the tables in silo-server docs/design/2026-08-16-intro-skip-mode.md.
   - Markers: the playing file's catalog version gives the first occurrence of each kind at once; `GET
     /api/v2/markers/files/{file_id}` then gives every occurrence (`marker_segments`) and has the server fill on-demand
     online markers for that file, as the TV apps' watch read does. One read per file per attempt; a failed read keeps
     the version's markers. Bale has no playback realtime connection, so markers the server detects after the start
     (`markers_updated`) arrive only with the next playback.
   - Intro, by Settings › Playback › Skip Intros (`playback.intro_skip_mode`). Ask: "Skip Intro" once playback runs inside
     an intro not yet decided, for 5 s; the fill grows to full as the timer ends. Select seeks to the intro's end; Back
     hides it and decides the intro (consumed, so a second Back acts as usual); the timer running out or a seek out of the
     intro hides it without deciding (scrubbing back in offers it again, with a full timer). A pause freezes the timer at
     once, a stall or reload after 1.5 s. Always: skip to the end at once and show "Watch Intro" under "Intro skipped" for
     5 s, pinned to that intro while playback runs on; Select plays the intro from its start; the timer, Select and Back
     decide it. Never: nothing. A mode change mid-intro re-evaluates it. Decisions last for the attempt (Retry and Up Next
     start over).
   - Recap and credits: "Skip Recap" / "Skip Credits" while the playhead is inside an occurrence (recap first), with no
     timer; not while the intro pill shows or a mount reloads. Select seeks to that occurrence's end, never over a
     mid-credits scene. Skip Credits in Settings (`playback.auto_skip_credits`): Android TV stores the switch but its
     player never reads it, so this follows tvOS `CreditsAutoSkipPolicy`: entering a credits occurrence while playing
     skips it once, and a Skip Credits press marks its occurrence too.
   - Seeks go through the skip queue (`QueueSeekTo`, the triggers' and scrubber's path), clamped to the file, with no skip
     chip; a seek still in flight counts at its target. A target at the file's end ends the stream, which raises Up Next or
     leaves as any end does. Credits never move Up Next (it opens at `next_up_prompt_seconds`, Android `e580e1811`); the
     series-page return now reads the closing credits' start (`finalCreditsStart`), past a mid-credits scene.
   - Look: bottom-right, 32 from the edge and 56 above the bottom, raised to 200 (220 ms) while the controls show; black
     65% capsule with a 2 px ring, white 25% resting and white focused; label 18sp Medium, white 55% resting and white
     focused; fill white 14% / 40%; caption 14sp at 70%; 200 ms fade in, gone at once.
   - Focus and keys: the intro pill takes focus as it appears, also with the controls up, but not during a scrub preview
     or a held trigger; the recap/credits pill never does. Controls hidden: A / Enter press the visible pill wherever focus
     is, instead of the owner's pause (Android routes D-pad Center to the pill the same way); Back dismisses the intro
     pill; the D-pad brings up the controls with Play/Pause focused, as always. Controls up: Up from the bar reaches the
     pill, Down goes back to the bar, Up/Left/Right stay on it (the triggers still skip); A presses what has focus. A pill
     that hides with focus hands it to the bar (controls up) or the page; a focused pill keeps focus when the controls
     hide. Back goes to a scrub preview first, then the intro pill, then the buffering pill, then the controls. Options,
     Up Next and the error screen take the pill down (its timer runs on underneath, and a pill that comes back takes focus
     again); a track menu only hides it. While a skip pill shows, the buffering pill waits (hidden, timer frozen).
   - Not mirrored: the Options Video tab's Skip intros row (`TvPlayerHud.kt`), Watch Party's room rules, and the web's
     untimed Skip Intro that comes back with the controls after the ask timer (server `594e8f1e2`; neither TV app has it).
7. Later: trickplay, subtitle delay/appearance.

