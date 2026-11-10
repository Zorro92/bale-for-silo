# Bale — guide for coding agents

Bale is a native Xbox client for [Silo](https://github.com/Silo-Server). The aim is a client the Silo
maintainers could adopt, so it follows Silo's conventions rather than inventing its own.

## The one rule: match the official Silo TV apps

Silo's Android TV and Apple TV apps share one TV design, **Skyline**. Bale must match them in
**look and behavior**: layout, sizes, type, colors, focus, navigation, Back, scrolling, empty and
error states. When in doubt, find out what the official app does and copy it. Suggestions from the
project owner are welcome, but the default answer is always "what do the official clients do?"

Where to look:

| Question | Source |
|---|---|
| Sizes, colors, type, components | `Silo-Server/silo-android` → `androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/{theme,components,shell,screens}` |
| Information architecture, focus model, motion | `Silo-Server/silo-apple` → `docs/skyline-design-guide.md`, `docs/tvos-focus.md` |
| What a screen looks like **today** | The real app: `scripts/silo-tv-reference.sh` runs the newest official Android TV APK in an emulator here (screenshot it next to Bale). The repo PNGs (`ui-validation/`, `tvos-detail/`) are months old and no longer match. |
| API behavior and payloads | `Silo-Server/silo-server` → `contracts/api/v2/openapi.json`; the Android `shared` module for how clients use it |

Read the Kotlin with `gh api repos/Silo-Server/silo-android/contents/<path> --jq .content | base64 -d`.

**Unit mapping.** Xbox lays out at 960×540 effective pixels, the same canvas as Android TV's dp, so
Android geometry maps 1:1 (`88.dp` → `88`). Android text is `sp × 0.86` (the TV app's
`TvUiFontScale`), so `15.5.sp` → `13.3`. The tvOS/mockup values in the Skyline guide are 1920×1080
px: halve them. Prefer the Android code's values when the two disagree; they're what ships.

## Layout of the repo

| Path | What | Builds on |
|---|---|---|
| `src/Bale.Core` | Silo API client (Kiota, generated), sign-in, sessions, browse/search models | Linux, macOS, Windows |
| `tests/Bale.Core.Tests` | xUnit tests for Core | anywhere |
| `tools/Bale.Cli` | `bale-cli`: runs Core against a real server (keeps a dev session) | anywhere |
| `src/Bale.Xbox` | The Xbox app: UWP on .NET 10, Native AOT, XAML | Windows only (CI) |
| `native/bale-gpu` | Rust GPU/media layer (`bale_gpu.dll`, C ABI, `windows` crate, static CRT); CI builds it before the MSIX | Windows (CI); `cargo check --target x86_64-pc-windows-msvc` anywhere |
| `src/Bale.Xbox/Themes/Skyline.xaml` | **Every** color, size and control style | — |
| `scripts/` | API generation, deploy, logs, screenshots, remote control | Linux |

Keep logic in `Bale.Core` wherever possible so it's testable on Linux. Don't edit
`src/Bale.Core/Api` by hand; add an `--include-path` to `scripts/generate-api.sh` and regenerate.
The API contract is pinned to a silo-server commit in that script.

## Developing (Linux only; nobody here has Windows)

```sh
export PATH=$HOME/.dotnet:$PATH          # .NET 10 SDK
dotnet test tests/Bale.Core.Tests         # Core, ~1 s
dotnet run --project tools/Bale.Cli -- home   # real home rows (after `bale-cli login <server>`)
```

The Xbox app only compiles on Windows: push to `main` and GitHub Actions builds a signed sideload
MSIX (~2 min). Then, with `XBOX_HOST` set to the console's IP:

```sh
./scripts/deploy-xbox.sh        # install the CI build of HEAD (waits for CI) and launch it
./scripts/bale-debug.sh log     # follow the live log
./scripts/bale-debug.sh press down down a     # real gamepad input: up down left right a b menu view lb rb lt rt
./scripts/bale-debug.sh key escape            # real keystrokes
./scripts/bale-debug.sh type dune             # typed text
./scripts/bale-debug.sh focus                 # what has focus, and its text
./scripts/bale-debug.sh where                 # page, overlays, focus, status and on-screen controls
./scripts/bale-debug.sh wait page Detail      # wait up to 8 s, then print where (also loaded / text / focus)
./scripts/bale-debug.sh wait --timeout 12000 text "Clear filters"  # 0–15000 ms; HTTP 408 keeps the where body
./scripts/bale-debug.sh caps                  # installed decoders, display modes, audio mix and baseline
./scripts/bale-debug.sh caps gpu              # D3D11 decode; D3D12 app-mode adapters/compute, game mode and console model
./scripts/bale-debug.sh playtest "https://example.org/public.mp4" 3  # muted built-in-player probe; 1–10 s (default 3)
timeout 300 ./scripts/bale-debug.sh playtest suite 3  # Chromium codec samples, sequential; needs a larger outer timeout
./scripts/bale-debug.sh playtest-ffmpeg @/tmp/x 30 software  # debug FFmpegInteropX; URL, @file or suite; 1–60 s; auto|software|system
./scripts/bale-debug.sh playtest-ffmpeg @/tmp/x 30 software threads=4 10bit=off rate=0.75 ahead=8 delay=1 prio=1  # debug-only tuning, no rebuild
./scripts/bale-debug.sh ffmpeg policy        # next real-player attempt; policy (default)|off; HLS unchanged
./scripts/bale-debug.sh suboverlay load "https://example.org/x.ass" [font-url...]  # own subtitle renderer over the open player (.srt/.vtt by extension, else ASS); also clear, stats (default)
./scripts/bale-debug.sh goto "Unwatched#2"     # setup only: focus the second matching control
./scripts/bale-debug.sh targets               # controls goto can reach
./scripts/bale-debug.sh tree                  # visible element tree with bounds
./scripts/xbox-screenshot.sh out.png          # what's on the TV
./scripts/xbox-logs.sh                        # the log file (survives crashes)
```

Use `targets` and `goto` only to get into position for a test. `goto` matches a control's name,
automation name or read text (case-insensitively); append `#N` for the Nth match. It refocuses once when
a rail redirects entry to its remembered item, and exits non-zero on no match or if focus still lands
elsewhere, so chain the next keys with `&&`. Drive the behavior under test with real `key`/`press` input,
not `goto`.

**Verify every UI or behavior change on the console** by driving it with `bale-debug.sh` and taking a
screenshot. Don't report a change as working from a successful build alone. The debug server
(`Services/DebugServer.cs`, port 8790) is compiled into development builds only; build anything
shipped with `-p:BaleDebugServer=false`.

## Focus building blocks (use them on every page)

Most focus bugs so far were the same few, re-solved page by page. Don't hand-roll these:

- **Every horizontal row is a `Services/Rail`** (`Rail.Attach(listOrItemsControl)`): Left/Right stop
  at its ends instead of jumping to another row, entering it lands on the remembered or selected item,
  the focused card pins to the start padding, and its bring-into-view never scrolls the page. Pages
  move vertically only on purpose (`Entered` → row pinning, section anchors), as the official apps do.
- **Horizontal card rails use `SkylineCardRailStyle`** (or a Skyline style based on it), with their
  card dimensions supplied to `Services/RailLayout`. Never set page-local rail padding, panel
  headroom, clipping overrides or compensating margins: the shared layout derives the zoom/ring/glow
  envelope from `CardRailMetrics` and keeps it inside the scroll viewport. Keep resting insets in
  the theme's derived styles. Search/Person remain vertical grids and use `CardView.GridHeadroom`
  for the same envelope inside each item. Run `python3 scripts/check-rail-layout.py`; a non-card
  horizontal list (tabs, filter pills, actions, metadata) must have an immediately preceding
  `<!-- rail-layout: non-card — reason -->` comment (a `//` comment for code-built non-card lists).
  Declare card lists in XAML so they can use the style without Native AOT resource lookups.
- **Grids** stop Left/Right at row ends too (see `SearchPage.OnResultsPreviewKeyDown`).
  `CatalogGridPage` does the same (`CatalogGridNavigation`). One deliberate exception, matching the
  official library screen: in Library Browse, Right from a row's last card enters the A–Z rail at
  All (`OnGridRightEdge` in `CatalogGridPage.Library.cs`) and Left returns to the remembered card.
  Don't copy it to other grids.
- **Bumpers, triggers and Page Up/Down never page a ScrollViewer**, app-wide
  (`App.OnPreviewKeyDown`). Only the player maps LT/RT to dedicated skip back/forward (see below);
  elsewhere they do nothing, keeping focus on screen.
- **Back to a page restores its focus** through `IRestoresFocus` + `FocusRetry`.

## Platform docs (check before guessing)

Look up UWP media and Xbox app behavior in Microsoft's docs before guessing:

- [UWP audio, video and camera](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/)
  (most useful): MediaPlayer, MediaSource/AdaptiveMediaSource, codecs, HDR, audio, timed text,
  custom media sources.
- [Xbox HEVC/4K guidance](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/hevc-xbox):
  `hevcPlayback`, ProtectionCapabilities checks, extended memory, display modes (60 Hz with
  3:2 pulldown; no 120 Hz media modes), `MF_MT_DECODER_USE_MAX_RESOLUTION` and DPB count.
- [UWP on Xbox](https://learn.microsoft.com/en-us/windows/uwp/xbox-apps/) and the general
  [UWP docs](https://learn.microsoft.com/en-us/windows/uwp/): app model, suspend/resume,
  capabilities, system resources (Series apps get D3D11/D3D12 at FL 11.0; see docs/player-plan.md
  for what was measured).

## Playback decoder policy

- FFmpegInteropX packages, native activation and license texts ship in **all** builds. Keep the CI
  package checker; CsWinRT's projection alone does not register the WinRT factories for Native AOT.
- Native FFmpeg is **8.1.2-bale.dav1d.1.5.4.p1**, not public 8.1.2 (which embeds dav1d 1.0.0).
  Keep wrapper `FFmpegInteropX.UWP.Lib` on the pinned **2.1.0-bale.N** fork (currently **2.1.0-bale.21**),
  built by `ffmpeginteropx-wrapper.yml`. `scripts/ffmpeg-native.json` and
  `scripts/ffmpeginteropx-wrapper.json` pin their release tags and SHA256s; run
  `scripts/restore-ffmpeg-native.ps1` before Windows restore/build. `NuGet.Config` maps both IDs only
  to the verified local feed. CI checks the actual MSIX's dav1d version and compares the wrapper DLL
  and provenance hashes with the pinned wrapper package.
  Rebuild with the manual `ffmpeg-native.yml` workflow (repository/ref/dav1d-tag inputs), preserving
  all other upstream submodule pins and FFmpeg flags: GPL/version3/encoders remain disabled;
  never enable x264/x265. FFmpeg source changes go only through reviewed patches in
  `scripts/ffmpeg-native-patches/` (named by the `patch_revision` input/pin, e.g. `p1`); the
  build script allows them to touch only `libavcodec/libdav1d.c`. Releases are immutable. See `docs/player-plan.md` for provenance and
  rebuild steps; do not overwrite assets or update a pinned hash without reviewing the new build.
- The wrapper fork pipeline is `ffmpeginteropx-wrapper.yml`; its patches must not change the IDL.
- Software AV1 has one path: libdav1d decodes into `bale_gpu` zero-copy D3D12 buffers, then the D3D12
  direct write (`bale_zc_*`, fused GPU grain) or the D3D11 landing fallback (`bale_zc_repack` +
  `bale_grain_gpu_*`) fills the pooled texture. 8- and 10-bit 4:2:0 (Main profile) only; GPU grain only.
  Anything zero-copy can't take, and any zero-copy failure, goes to server transcode. There is no CPU
  grain, staging upload, swscale thread pool, upload variant choice, debug repack or upload/zero-copy probe.
- All builds tune software AV1 with `bale_decode_ahead_frames=16` and a 1000 ms
  MSS BufferTime. Above 1080p `SoftwareDecodeTuning` trims decode-ahead to 8 and BufferTime to 500 ms
  (~460 MiB less at 4K, console-measured) and sets `bale_dav1d_max_frame_delay=6` (width > 1920 or
  height > 1080), using route dimensions from the source for original delivery or the effective
  recipe with source fallback otherwise. Unknown size leaves delay unset; hardware/system video
  gets none of these keys. This tuning does not expand the measured software-video capability bounds.
- `SoftwareDecodePolicy.VideoCodecs` is the project owner's explicit **AV1-only** software video
  allowlist. Add nothing without them. Other unsupported video goes to server transcode, never
  FFmpeg software or D3D11. Measured AV1 evidence is 0.1.230: 8/10-bit, **1080p30**, not 4K.
- `PlaybackRoutePolicy` decides each mount. HLS always stays on AdaptiveMediaSource. Direct AV1
  forces FFmpeg software video; audio-only fallback forces the system video decoder and validates
  reported engines before playback. Direct MKV/WebM always uses the FFmpeg demuxer with system
  video (Windows' MKV source over-reads ~2.2x and stalls on sparse subtitle tracks); MP4 stays on
  Windows' source (measured equal on both). AAC/MP3 use the pinned winmd's system audio switches;
  AC3/EAC3 reach the system Dolby decoder undecoded via patch 0021 (`bale_system_decoder_dolby`,
  keeps E-AC-3 JOC/Atmos). Passthrough remains unclaimed.
- FFmpeg audio selection must honor the server's ordinal, not FFmpeg's reordered default:
  map `AudioStreams[].StreamIndex` back to demux order, then set the item's AudioTracks.SelectedIndex
  before Play. Fail closed if the selected stream is missing.
- Display HDR is detected from HDMI modes on the UI thread at player start, including debug
  FFmpeg overrides. The bare baseline is unknown, not a hardcoded SDR display. Claim only HDR10
  from non-stereo PQ modes; HDR10+/HLG/DV remain unverified. HDR on a non-HDR display must be
  server-transcoded/tone-mapped, never console tone-mapped. Direct HDR10 switches HDMI to PQ
  before mounting; restore the default on stop/error, recovery, unload/navigation and suspend.
  A failed switch uses the single replan with all HDR claims withdrawn, not a codec withdrawal.
  Keep the manifest's `hevcPlayback` capability (required for Xbox HDR10/4K, with media memory
  and app/game concurrency implications). Never select 120 Hz for media; use at most 60 Hz.
- An FFmpeg create/open/MediaFailed failure gets the existing single replan with its triggering
  codec claims removed and an explicit server-transcode reason; never retry the same capability.
- Debug real-player override is **policy** (default) or **off** only; off also withdraws fallback
  claims. The standalone `playtest-ffmpeg` probe retains its experimental modes. See docs/player-plan.md.
- Never print media URLs, stream grants, headers or media/session exception messages. Log only safe
  HRESULTs, IDs/codecs and fixed diagnostics; keep FFmpeg's native log provider disabled.

## Player skip transport

- While the player is showing, **LT / keyboard J skips back 10 s**, **RT / keyboard L skips forward
  30 s**. Test with `scripts/bale-debug.sh key j`, `key l`, `press lt` and `press rt`. Both real analog
  trigger injection and keyboards go through the root preview-key path. Only the first DOWN acts;
  repeats and key UP are consumed, with no hold-to-scrub (`silo-android androidTvApp/.../player/TvPlayerRemoteKeyAction.kt`).
- Intervals are `TvPlayerScreen.kt`'s `LEGACY_TV_VIDEO_SEEK_INTERVALS`, also the defaults in
  `shared/.../model/settings/SeekIntervals.kt`: 10 back / 30 forward. Clamp to 0 / known movie
  duration. Rapid presses accumulate from the last requested source target, not a stale player clock.
- Skips and `bale-debug.sh seek` share `PlayerPage.Seek.cs`'s absolute-source path. A local seek waits
  for `SeekCompleted`; a target outside a produced/reanchored HLS window uses negotiated protocol-v3
  `seek_reanchor`, retaining the same route, plan, session and tracks. This does not spend failure
  recovery budget. Seek completion flushes the actual landed source position through the sequenced
  progress reporter/journal, including short clamped seeks; pause state is preserved.
- Passive feedback copies `TvSkipSeekIndicator.kt`, `TvPlayerScrubber.kt` and `TvPlayerChrome.kt`:
  a signed skip/landing-time chip, resting 4dp track (white at 22% / Paper fill), elapsed and
  minus-remaining / wall-clock end time. Inter uses the TV 0.86 text scale; Android dp is 1:1 with
  Xbox effective pixels. No puck or focusable scrubbing, no transport buttons yet (the official
  44dp button metrics live in `TvPlayerTransportVisualPolicy.kt`). Every press restarts the official
  `SKIP_FEEDBACK_HIDE_MS = 1200` timer. Feedback never moves focus or changes Back / I / View.

## Player diagnostics

- **The console throttles the app about 10–15 s after the last controller or keyboard input**, on
  any page and for the whole console. CPU work and DRAM copies get several times slower; GPU memory
  access does not. A film is watched in that state, so measure in it: no input after starting
  playback, and take every reading (CPU, stalls, memory, timings) at the same offset from the last
  key, well past 15 s. A reading right after a key press is not comparable to an idle one.
  Debug-server calls (`seek`, `stats`, `mem`, `threads`) are not input. Never inject F24 to keep the
  console awake: the shell opens the Guide's Capture panel on it. No app-level API tried so far holds
  the fast state. Measurements and what was ruled out: `docs/player-plan.md`, "no input" A/B.
- Debug probe syntax: `playtest-ffmpeg <url|@file|suite> [seconds] [auto|software|system]
  [threads=N] [10bit=on|off] [rate=R] [ahead=N] [delay=N] [prio=N] [maxres=0|1|default] [dpb=N|default]
  [hdr=on|off|default] [grain=dav1d|gpu|off|on|default]`. Supply seconds and mode before
  tuning; keys may be in any order, at most once each. Seconds are 1–60 (default 3), mode defaults
  to auto, threads are 0–16 (0 = logical CPU count), and rate is an invariant decimal 0.5–2.0
  (default 1). `ahead` is 0–64 decoded frames (0 = off),
  `delay` is 0–64 dav1d frames (0 = dav1d default), and `prio` is 0–2 (0 = off,
  1 = THREAD_PRIORITY_ABOVE_NORMAL, 2 = THREAD_PRIORITY_HIGHEST; default unset). Software-mode
  probes use the release ahead=16 default and the production zero-copy path; URI probes have unknown dimensions before creation, so delay stays
  unset unless overridden (use delay=6 for a known above-1080p comparison). Auto/system probes
  retain the wrapper defaults unless overridden; threads/10bit defaults are 0/true. `10bit=off`
  requests 8-bit NV12 instead of 10-bit P010 for 10-bit AV1. The response reports applied values/defaults
  for the fork keys, actual config values, logical CPUs,
  applied playback rate and decoded MSS output subtype when available. Ratio/verdict compare media
  advance with wall time × applied rate, not unadjusted wall time. These overrides affect only that
  probe, never the real player, and are absent with `BaleDebugServer=false`; release defaults remain.
- Software-video probes reuse the player's AOT-safe `DebugFrameDelivery` observer. Late totals and
  min/median sample-end-ahead (floored at zero, sampled **at delivery**) exclude preroll, paused or
  buffering delivery, seeks and discontinuities. Total delivered counts all observed video samples
  except seeks, including preroll/non-playing frames. Min/median collection is opt-in for the bounded
  probe only; the regular player retains no per-frame history. System-video/audio-only/no-sample
  probes report unavailable, not zero. Neither late delivery nor delivered-ahead measures renderer
  drops/queue depth. The fork consumes decimal-string `FFmpegOptions` keys
  `bale_decode_ahead_frames`, `bale_dav1d_max_frame_delay` and
  `bale_decode_thread_priority` itself, never passing them to FFmpeg. Bale sets the software-video
  defaults listed above in every build; the priority key is debug-only and remains unset in production.
- `ffmpeg-tune` also accepts `ahead=N|default`, `delay=N|default` and `prio=N|default`
  with the same ranges. These memory-only real-player overrides are captured per attempt; omitted keys
  keep their values, `default` clears only that override and restores the release setting (prio unset),
  and invalid updates change nothing. Explicit values win, including zero for ahead/delay/prio. These
  three overrides apply only to software video; they do not affect an open player, native/HLS routes or
  the standalone probe. Production uses the release defaults without debug overrides.

- `scripts/bale-debug.sh stats` prints all current player rows as `Label: value`, marking overlay-hidden
  rows `[hidden]`; no active player exits non-zero. `stats-rows [list|hide <labels>|show <labels>|only
  <labels>|reset]` filters only the overlay using one comma-separated argument of exact, case-insensitive
  labels (for example, `stats-rows hide "Read-ahead,Stream reads,Dropped frames"`). Labels outside the
  known overlay rows are accepted and reported as unknown; `only` also hides future unlisted rows.
  Changes re-render a visible panel immediately, also work before a player opens, and reset on app
  restart; `where` keeps every row. `Dropped frames` (always unavailable) is hidden by default;
  `reset` restores that default and `show "Dropped frames"` brings it back.
- `mem [gc]` reports the managed GC heap/committed bytes, collection counts and
  `MemoryManager.AppMemoryUsage`; "non-GC" is app usage minus GC committed (native decoders, Media
  Foundation, WinRT, Composition). `gc` first forces two full blocking collections with finalizers, so
  memory that drops afterwards was only awaiting collection, not leaked.
- `I` / gamepad View toggles the read-only stats panel; `scripts/bale-debug.sh where` includes the
  same rows even when hidden. Process CPU is the kernel+user `GetProcessTimes` delta divided by
  wall time: **cores**, then cores / `Environment.ProcessorCount` as a percentage, and that logical
  CPU count. This is app-process load, not system-wide CPU or a single decoder thread's load.
- **Late frames** is available only for FFmpeg software video. The pinned FFmpegInteropX
  `Source/FFmpegMediaSource.cpp` `OnSampleRequested` assigns decoded video samples synchronously;
  a later observer compares each sample's PTS with `MediaPlaybackSession.Position` at delivery.
  The count is per mount, with a rolling last-10-wall-second count/rate. Preroll, paused/buffering
  delivery, discontinuities and seeks are excluded. **Delivered ahead** is the last decoded sample
  end minus the current player clock, floored at zero; it is not a renderer queue depth.
- UWP `MediaPlayer` / `MediaPlaybackSession` exposes no renderer-drop statistics or decoded queue.
  Native direct, HLS and FFmpeg system-video decoding therefore show late delivery/ahead as
  **unavailable**. Actual dropped frames are unavailable on every route. Never infer drops from
  buffering, clock polling, expected FPS, or the 23.976-on-59.94 display cadence. The MF media-engine
  statistics interface is not an interface exposed by this WinRT MediaPlayer pipeline.

## Native AOT + UWP pitfalls (all hit for real)

Each of these crashed or silently broke the app on the console while compiling fine:

- **Don't read XAML resources from code** (`Application.Current.Resources["..."]`): `InvalidCastException`.
  Style in XAML; switch looks with visual states (`VisualStateManager.GoToState`), `ToggleButton`
  `Checked`, or templates. Colors needed in code are literals.
- **Focus events report list containers as `ContentControl`**, never `ListViewItem`/`GridViewItem`.
  Match `e.OriginalSource is ContentControl { Content: CardView v }`. Same caution for `sender`:
  match on `Control`/`FrameworkElement`.
- **`ContentPresenter` doesn't project**: from `GetTemplateChild`, the visual tree or focus events it
  comes back as a plain `FrameworkElement`, so `(ContentPresenter)x` throws and `is ContentPresenter`
  is false. Use a `Border` (set `Child`) for slots you fill from code (`PageFrame`).
- **`ListView.ContainerFromIndex` returns null** for the rails. Find containers in
  `ItemsPanelRoot.Children` by `Content`.
- **Classes handed to XAML lists must be `partial`**, and the project needs `AllowUnsafeBlocks`
  plus `CsWinRTAotOptimizerEnabled=Auto` (already set), or `ItemsSource` throws `E_INVALIDARG`.
- **No implicit transitions (`BrushTransition`, `ScalarTransition`) inside `ControlTemplate`s**: the
  first layout crashed natively. They're fine on plain elements.
- **Never put `ScaleTransition`/`Scale` on a list item's template root.** Gamepad A's press tilt
  sets a `Projection` on that root, and UWP throws `UnauthorizedAccessException` ("Calling
  Projection API is not allowed ... ScaleTransition") and the app ends. Keyboard Enter doesn't
  tilt, so test focus lifts with `press a` too. Scale an inner element (`Art`, `TrailerLift`).
- **A template that presents `Content` twice must get a string**, never an element (one element
  can't have two parents). `SkylineTabStyle` does this.
- **No XAML `OpacityTransition` on an element you also use with `ElementCompositionPreview`**;
  animate in Composition instead. **Composition mask brushes can't be nested**; combine masks into
  one image (`Assets/ArtMask.png`).
- **UWP doesn't clamp `CornerRadius`**: a radius bigger than half the height draws an oval. Pills
  get a fixed height and radius = height / 2 (`SkylineTabHeight`/`SkylineTabPill` etc.).
- **An exception thrown from a handler attached with `AddHandler(..., handledEventsToo: true)`
  ends the app without reaching `UnhandledException`** and without a log line. Wrap those
  handlers in try/catch and log.
- **Icons are inline `Path Data="M…"` strings** inside a `Viewbox` (Path 24×24 for Material data).
  UWP can't build a `PathGeometry` resource from a `Figures="M…"` string (WMC0055), and a
  `PathIcon` doesn't scale its geometry, so 24-unit data in a smaller box draws as a clipped blob.
  To switch icons by state, stack one `Path` per icon and toggle `Visibility`.
- `DebugSettings.XamlResourceReferenceFailed` is WinUI 3 only. `GettingFocus` args are
  `GettingFocusEventArgs`. Injected gamepad buttons are `Windows.Gaming.Input.GamepadButtons`.
- Windows can only partly draw SVG; Silo's DiceBear preset avatars load as PNG
  (`ProfileAvatars.ImageUrl`).

When the app dies with nothing logged, it's native: add `Log.Info` breadcrumbs, redeploy, and read
`xbox-logs.sh` to see the last step reached.

## Conventions

- Push straight to `main`; CI must stay green (Core tests on Linux, MSIX on Windows).
- Commit messages: imperative subject, a body that says *why*, and end with the agent's
  `Co-Authored-By` line. Don't put backticks in `git commit -m` inside double quotes (the shell runs them).
- Comments explain why, and cite the official client file a behavior or value came from.
- The name is **Bale**, "a client for Silo". Silo's trademark policy forbids naming it "Silo …".
  AGPL-3.0, like Silo. Third-party assets are listed in `THIRD_PARTY_NOTICES.md`.
- Never commit server addresses, console IPs, tokens or API keys; scripts read `XBOX_HOST` from the
  environment.
