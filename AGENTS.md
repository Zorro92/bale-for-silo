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
./scripts/deploy-xbox.sh        # install the newest CI build and launch it
./scripts/bale-debug.sh log     # follow the live log
./scripts/bale-debug.sh press down down a     # real gamepad input: up down left right a b menu view lb rb lt rt
./scripts/bale-debug.sh key escape            # real keystrokes
./scripts/bale-debug.sh type dune             # typed text
./scripts/bale-debug.sh focus                 # what has focus, and its text
./scripts/bale-debug.sh tree                  # visible element tree with bounds
./scripts/xbox-screenshot.sh out.png          # what's on the TV
./scripts/xbox-logs.sh                        # the log file (survives crashes)
```

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
- **Bumpers, triggers and Page Up/Down do nothing**, app-wide (`App.OnPreviewKeyDown`); the official
  app handles none of them, and a ScrollViewer would page away from focus.
- **Back to a page restores its focus** through `IRestoresFocus` + `FocusRetry`.

## Native AOT + UWP pitfalls (all hit for real)

Each of these crashed or silently broke the app on the console while compiling fine:

- **Don't read XAML resources from code** (`Application.Current.Resources["..."]`): `InvalidCastException`.
  Style in XAML; switch looks with visual states (`VisualStateManager.GoToState`), `ToggleButton`
  `Checked`, or templates. Colors needed in code are literals.
- **Focus events report list containers as `ContentControl`**, never `ListViewItem`/`GridViewItem`.
  Match `e.OriginalSource is ContentControl { Content: CardView v }`. Same caution for `sender`:
  match on `Control`/`FrameworkElement`.
- **`ListView.ContainerFromIndex` returns null** for the rails. Find containers in
  `ItemsPanelRoot.Children` by `Content`.
- **Classes handed to XAML lists must be `partial`**, and the project needs `AllowUnsafeBlocks`
  plus `CsWinRTAotOptimizerEnabled=Auto` (already set), or `ItemsSource` throws `E_INVALIDARG`.
- **No implicit transitions (`BrushTransition`, `ScalarTransition`) inside `ControlTemplate`s**: the
  first layout crashed natively. They're fine on plain elements.
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
