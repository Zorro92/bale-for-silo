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
./scripts/bale-debug.sh playtest "https://example.org/public.mp4" 3  # muted built-in-player probe; 1–10 s (default 3)
timeout 300 ./scripts/bale-debug.sh playtest suite 3  # Chromium codec samples, sequential; needs a larger outer timeout
./scripts/bale-debug.sh playtest-ffmpeg @/tmp/x 30 software  # debug FFmpegInteropX; URL, @file or suite; 1–60 s; auto|software|system
./scripts/bale-debug.sh ffmpeg policy        # next real-player attempt; policy (default)|off; HLS unchanged
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
- **Bumpers, triggers and Page Up/Down do nothing**, app-wide (`App.OnPreviewKeyDown`); the official
  app handles none of them, and a ScrollViewer would page away from focus.
- **Back to a page restores its focus** through `IRestoresFocus` + `FocusRetry`.

## Playback decoder policy

- FFmpegInteropX packages, native activation and license texts ship in **all** builds. Keep the CI
  package checker; CsWinRT's projection alone does not register the WinRT factories for Native AOT.
- Native FFmpeg is **8.1.2-bale.dav1d.1.5.4**, not public 8.1.2 (which embeds dav1d 1.0.0).
  Keep wrapper `FFmpegInteropX.UWP.Lib` at 2.1.0. `scripts/ffmpeg-native.json` pins the release and
  SHA256; run `scripts/restore-ffmpeg-native.ps1` before Windows restore/build. `NuGet.Config`
  maps this package only to the verified local feed. CI checks the actual MSIX's dav1d version.
  Rebuild with the manual `ffmpeg-native.yml` workflow (repository/ref/dav1d-tag inputs), preserving
  all other upstream submodule pins and FFmpeg flags: GPL/version3/encoders remain disabled;
  never enable x264/x265. Releases are immutable. See `docs/player-plan.md` for provenance and
  rebuild steps; do not overwrite assets or update a pinned hash without reviewing the new build.
- `SoftwareDecodePolicy.VideoCodecs` is the project owner's explicit **AV1-only** software video
  allowlist. Add nothing without them. Other unsupported video goes to server transcode, never
  FFmpeg software or D3D11. Measured AV1 evidence is 0.1.230: 8/10-bit, **1080p30**, not 4K.
- `PlaybackRoutePolicy` decides each mount. HLS always stays on AdaptiveMediaSource. Direct AV1
  forces FFmpeg software video; audio-only fallback forces the system video decoder and validates
  reported engines before playback. AAC/MP3 use the pinned winmd's system audio switches; no
  AC3/EAC3 or passthrough switches exist in 2.1.0. Passthrough remains unclaimed.
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

## Player diagnostics

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
