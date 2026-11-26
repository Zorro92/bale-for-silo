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

**Playback follows the Apple TV app (owner, 2026-11-17).** For playback planning and routing (protocol
features, capability claims, routes, display criteria, HDR/Dolby Vision), the reference is
`silo-apple` and its engine **AetherEngine** (`Silo-Server/AetherEngine`, LGPL-3: read for behaviour,
copy no code), not Android: Xbox is the closer platform (fixed box, system HDR/DV pipeline, FFmpeg
demux feeding a system player). Follow it wherever Xbox hardware allows; where it can't (Xbox does
not map Dolby Vision for non-DV TVs), the owner's rules below apply. The Skyline UI rule above is
unchanged. Gap analysis and phases: docs/apple-playback-parity.md.

**Deliberate deviations.** These differ from the official apps on purpose. Keep them; don't
"fix" them back.

- **Experimental GPU film grain:** Settings › Playback offers a device-wide, persisted, off-by-default dav1d D3D11 GPU grain toggle (owner, 2026-11-19); it needs no zero-copy or D3D12.
- **SDR Brightness:** Settings › Playback › HDR on SDR TVs offers a device-wide, persisted SDR white for the console
  tone map of HDR video on an SDR TV: Default (100 nits) or 80/120/150/200/250/300 nits, sent as the fork's
  `bale_tonemap_white_nits` to Bale's stage and rav1g's display pass (owner, 2026-11-24; `SdrBrightness`,
  docs/gpu-tone-map.md). The official apps never tone-map on the device, so they have no such row.
- **Calendar focus** follows the selection: the selected pill/day, then the first card, with
  per-row memory.
- **Player controller map** (owner's design, 2026-11-16; media remote keys stay official):
  - D-pad on clean playback brings up the controls; further presses move between buttons.
  - A while playing pauses and brings up the controls; A while paused with the controls hidden
    resumes without them. With the controls up, A presses the focused button.
  - Triggers: a tap skips 10 s back / 30 s forward (on release); held past ~250 ms they rewind /
    fast-forward steadily from the current position with no skip first (owner 2026-11-17; no speed ladder).
  - Bumpers jump to the previous / next chapter.
  - The controls hide after a while without input.
- **Display resolution matching.** Auto version choice picks the file that best matches the
  connected display, not always the highest resolution (bandwidth and resources on 1080p TVs). It
  ranks only inside the current edition (the official Edition rule: Edition is the cut, history
  picks only the edition); an explicit pick still wins; unreadable display modes give the official
  server order. On by default; the toggle comes with the 0.2.x Settings menu. Decode claims stay
  decoder-based: the display only affects version choice. See `docs/player-plan.md`, "Display
  resolution matching".
- **Episode codes** are `S01E01` everywhere (`E01` without a season), as under the official season
  row; the official apps also use `S1:E2`, `S1·E2`, `S1 · E2` and `S1E2` elsewhere (owner, 2026-11-17;
  `EpisodeCode.Format`; reported upstream).
- **Home layout** matches the libraries: no 28-point foreground drop (official `TvHomeScreen.kt`
  `contentVerticalOffset = 28.dp`), so the title of the row after next stays on screen, as on Movies
  (owner, 2026-11-17; reported upstream).
- **Detail hero row memory** follows Apple TV, not Android's selectorFocus (owner, 2026-11-18; Apple
  `067e6cee`, `TVDetailActions.swift`): entering the action row lands on the action last focused there,
  else Play; Play stands in for one that can't take focus, Watchlist for Play; reset per page (`DetailHeroFocus`).
- **Series years on cards** follow Apple TV, not Android's single year (owner, 2026-11-18; Apple `9718f17b`,
  `CardYears.swift`): "2019 – 2023" once ended or cancelled, "2021 – Present" while returning, else one year
  (`CardYears`). Detail and the marquee keep one year, as on Apple TV.
- **Track picks across a Version or Edition change** follow Apple TV, not Android's onVersionSelected, which
  clears both (owner, 2026-11-18; Apple `417508d9`, `TVItemDetailView.swift`, `DetailPlaybackFormatting.swift`):
  a subtitle pick moves to the new file's track with the same language, forced and SDH flags (same full tag
  first, then the same title), else Auto; Auto and Off carry. A Version pick keeps an audio ordinal the new
  file has; an Edition pick that changes the file drops it.
- **Closed caption channels:** the subtitle menus offer every CEA-608 channel the stream carries, a row each (CC1 to
  CC4; a stream with CC1 alone keeps one "CC" row), where Android TV and AetherEngine show one CC1 track (owner,
  2026-11-26; `PlaybackClosedCaptions`, docs/closed-captions.md).
- **Closed caption flash:** 608 flashing characters blink, half a second shown and half a second gone with their row's
  background steady (47 CFR 15.119 (h)(2)), where Android TV (Media3) and AetherEngine draw flash steady (owner,
  2026-11-26: captions as authored; `subs::cc::ass`, docs/closed-captions.md).

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
| `native/bale-core` | Rust port of Bale.Core logic (`bale_core.dll` / `libbale_core.so`, JSON over a C ABI, docs/rust-port.md); build it (`cargo build --release --locked --features debug-probes`: the tests run debug-server code; bale-cli works with either) before `dotnet test` or bale-cli | Linux and Windows |
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
MSIX (~2 min).
Warnings are errors in every project (Directory.Build.props), the Windows Xbox build included, so a
new CsWinRT/nullability warning turns CI red instead of piling up in the log. Run
`scripts/install-git-hooks.sh` once per clone: the pre-push hook lints every Rust crate exactly as CI does
(`cargo fmt --check` and `cargo clippy --all-targets --locked -- -D warnings`, so test code is linted too, plus
bale-gpu for `x86_64-pc-windows-msvc` when that target is installed), rebuilds the Rust core and runs the
.NET tests, and stops the push on any failure or warning that Linux can see (`--no-verify` skips it). A push that changes only Markdown, `docs/` or `.claude/` skips all of it, as CI does. Agents push
with `scripts/push-main.sh` (stops at the first failure with its reason, then prints the CI run id); never chain a
CI wait after `git push` with `;`. Lint by
hand with the same `--all-targets` flags; plain `cargo clippy` skips tests.
XAML/CsWinRT warnings only show in the Windows build, but `scripts/check-cswinrt.sh` (skill `cswinrt-check`) catches
CsWinRT analyzer errors such as CsWinRT1028 on Linux in seconds; the pre-push hook runs it when C# inputs changed. FXC-only shader
errors (e.g. X3571 under `/WX`) likewise show up only in the Windows build: `scripts/check-fxc.sh` (`--self-test`
proves it fails) runs Microsoft's real fxc.exe, pinned with its SHA256 in `scripts/fxc-tool.json` and cached in
`~/.cache/bale-fxc`, under Wine in podman (`localhost/bale-fxc`, ~420 MB) through `build.rs` with `FXC=scripts/fxc-wine.sh`
(~30 s warm). The pre-push hook runs it when `native/bale-gpu/shaders/`, `build.rs` or the check's files changed, stops
on fxc errors and only warns when podman or the download is missing. Shaders stay SM5/FXC; a DXC pass is no evidence. CI prints every error and warning from a normal-verbosity
file log after each MSIX build (the minimal console once hid a CsWinRT error entirely), and a failed build whose
log names no error is rebuilt single-process at normal verbosity.
CI skips Rust tests when GPU/libass/toolchain/CI inputs match the last successful push and reuses the input/rustc/target-keyed DLL plus libass notices; PRs and manual runs always test Rust.
Bale is still in its build/debug phase, so pushes build only the debug-server package. Don't
dispatch CI with `production=true` (the production package) unless the owner asks for it; debug-only
changes are covered by the debug build and the tests. Then, with `XBOX_HOST` set to the console's IP:

```sh
./scripts/deploy-xbox.sh        # install the CI build of HEAD (waits for CI) and launch it
./scripts/bale-debug.sh log     # follow the live log
./scripts/bale-debug.sh press down down a     # real gamepad input: up down left right a b menu view lb rb lt rt
./scripts/bale-debug.sh key escape            # real keystrokes
./scripts/bale-debug.sh type dune             # typed text
./scripts/bale-debug.sh focus                 # what has focus, and its text
./scripts/bale-debug.sh where                 # page, overlays, focus, status and on-screen controls
./scripts/bale-debug.sh alive                 # running / hung / stopped (+ newest crash dump) via Device Portal; failed or long requests say the same
./scripts/bale-debug.sh home                  # fresh Browse/Home for the current profile; clear Back/Forward history; never pick a profile
./scripts/bale-debug.sh open <content-id> [file=<fileId>]  # wait for Detail, select the exact version, focus Play
./scripts/bale-debug.sh play <content-id> [file=<fileId>] [start=<seconds>]  # normal Play path; wait for player state + plan delivery
./scripts/bale-debug.sh play-url <url> [start=<seconds>]  # debug HTTP file through the real player; no Silo session
./scripts/bale-debug.sh media-fault outage=40  # simulated server outage for every Rust media request/read (0 ends); no args = recovery counters
./scripts/bale-debug.sh media-fault empty-tail=3  # next HLS mount: the playlist's last 3 segments hold no media (server 4bee8f8)
python3 -I scripts/serve-media.py ./test-clips --port 8765  # read-only HTTP ranges; see --help for bind/firewall
./scripts/bale-debug.sh play                  # no id: resume the active player after a paused seek
./scripts/bale-debug.sh navlog [N]            # last 1–200 page/input/player events, default 50
./scripts/bale-debug.sh wait page Detail      # wait up to 8 s, then print where (also loaded / text / focus)
./scripts/bale-debug.sh wait --timeout 12000 text "Clear filters"  # 0–15000 ms; HTTP 408 keeps the where body
./scripts/bale-debug.sh caps                  # installed decoders, display modes, audio mix and baseline
./scripts/bale-debug.sh caps gpu              # D3D11 decode; D3D12 app-mode adapters/compute, game mode and console model
./scripts/bale-debug.sh audio-probe           # IEC 61937 bitstream/PCM formats the audio endpoint takes in exclusive mode now (IsFormatSupported only; docs/audio-passthrough.md)
./scripts/bale-debug.sh audio-out-test 5 440 channels=8  # bale_gpu exclusive-mode output (offload fallbacks): tone on front L/R, 2/6/8 ch 24-in-32, drift, underruns, sync; uisounds=off; 409 during playback (docs/audio-passthrough.md)
./scripts/bale-debug.sh audio-out-test variants  # Initialize variants (offload first, flags/timing, properties, formats, AC-3/E-AC-3): fresh client per row, never started
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

For playback setup, prefer `home` + `open`/`play` using content ids from `bale-cli search/detail`.
Home/Open wait up to 30 seconds; Play waits up to 90 seconds for ready/error. `start=` specifies
source seconds without resume rewind. All require the already selected profile. After two navigation
failures in a row, stop and use `home`/`open` instead of retrying keys. Use `navlog` and the local
`${XDG_STATE_HOME:-$HOME/.local/state}/bale-debug/commands.log` (override: `BALE_DEBUG_COMMAND_LOG`)
for diagnosis. The script logs each invocation, status and first response line with URLs/credentials
and typed text redacted; HTTP 404/408/409 also warn on stderr when stdout is redirected.

Use `targets` and `goto` only to get into position for a test. `goto` matches a control's name,
automation name or read text (case-insensitively); append `#N` for the Nth match. It refocuses once when
a rail redirects entry to its remembered item, and exits non-zero on no match or if focus still lands
elsewhere, so chain the next keys with `&&`. Drive the behavior under test with real `key`/`press` input,
not `goto`.

**Verify every UI or behavior change on the console** by driving it with `bale-debug.sh` and taking a
screenshot. Don't report a change as working from a successful build alone. The debug server
(`Services/DebugServer.cs`, port 8790, unauthenticated and reachable from the whole LAN) is compiled only into
builds that opt in with `-p:BaleDebugServer=true`: CI's debug-server package (the one `deploy-xbox.sh` installs)
and `scripts/check-cswinrt.sh`. The default build is the shipping one (no debug server, no `inputInjectionBrokered`,
channel `release`); never install a debug-server build on someone else's console. Store items: docs/store-readiness.md.

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
- **UI artwork is disposable residency:** background/suspend and pages underneath the player release decoded bitmaps, backdrops/masks, samples and shadows through `UiResidency`; restore visible art lazily from the unchanged disk cache, preserving focus/scroll/page state. Pause does nothing. See `docs/ui-memory.md`.

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

## Console resource limits (check every design decision against them)

Budget per part: docs/resource-budget.md (0.2.x goal; rav1g envelope now).
Owner rule (2026-11-20): every design decision (where work runs, buffer/pool sizes, threads, CPU vs GPU,
copies, bit depth) is checked against the measured limits and says which limit it is sized for. Source of
truth: docs/gpu-tone-map.md "Measured limits" (also the bale-projects skill, "Measured console limits").
For Bale, a normal app with hevcPlayback on the Series X:
- 3328 MiB app memory, one pool for CPU and GPU; GPU-resident memory ≤ ~2857 MiB (DXGI budget).
- ~1.86 TFLOP/s GPU compute (about 1/6 of the console); the queue isn't preempted.
- 4 cores (8 logical CPUs) shared with the system; throughput stops growing beyond 4 threads.
- The idle state (~10 s after the last input, i.e. all real playback) slows the CPU ~21-25%, CPU RAM copies
  3.6-4x and GPU copies ~2.4x. GPU compute isn't slowed.
- So: put heavy per-frame work in GPU compute; touch whole frames on the CPU at most once; avoid extra
  full-frame copies on either side. `expandedResources`/Game mode is not an option (Store rejects it).

## Playback decoder policy

- **Never let MF scale 4K SDR to a smaller display (2026-11-17):** on Xbox its video processor leaks
  driver state per open and Bale crashes at ~23 opens. The tone-map route passes the display size
  (`bale_tonemap_output_width/height`) and plain SDR larger than the display takes the
  `console_downscale` route (d3d11va + bale_gpu scale-only), both through bale_gpu's linear-light area
  downscale. Since wrapper bale.80 the same stage runs on FFmpeg software frames (phase B of
  docs/software-decode.md): a software H.264/HEVC/VP9 route takes both, and a software frame above 1080p plays only
  through it or on a display known to fit it. Details, evidence and gates: docs/player-plan.md "4K SDR downscale on
  smaller displays".
- **Bit-depth rule (owner, revised 2026-11-18):** work at source depth, deliver at display depth, AV1
  included (owner, 2026-11-26: its temporary 10-bit exception ended with rav1g as the default). Every stage that
  processes video (tone map, film grain, scaling, colour or format conversion, repack, FFmpeg sws/filters,
  bale_gpu shaders, the fork, rav1g) keeps the source's depth: 8-bit in, 8-bit out (NV12); more than 8 bits
  in, 10-bit out (P010; 12-bit is capped to 10). Never reduce processing depth. Don't raise 8-bit to
  10-bit either, with one exception where it makes sense: bale_gpu's SDR area downscale (the 4K-on-smaller-
  display route) writes P010 for 8-bit sources too, because averaging in linear light yields values between
  8-bit codes. AetherEngine does the same per stage (software decode, deinterlace, tone mapper and upscaler
  output the input's depth; AV1 grain at the stream's depth). The console tone map's inputs are all 10-bit,
  so it writes P010, and `FfmpegVideoOutputGuard` rejects NV12 stage output unless bale_gpu lacks P010 or
  Bale requested 8-bit for that mount. **Phase 2 (2026-11-19):** on an 8-bit SDR display (current HDMI mode
  below 30 bpp or unknown, not HDR passthrough, not debug `output-depth source`) the console stage (tone map
  or SDR downscale) writes **dithered 8-bit NV12** in the same pass instead of P010, rounding once at full
  precision (rav1g's R2 threshold since 2026-11-26, owner: no cost on the console bench; debug `tonemap-debug
  bayerdither` selects the static 8x8 Bayer it replaced; bale_gpu bit `BALE_TONEMAP_OUTPUT_NV12_DITHERED`). On the
  rav1g route the fork hands the same key to rav1g's display pass (tone map, or SDR scaled to the display:
  `Rav1gRouteDecision.DownscaleStage`), which writes ordered-dithered NV12 itself after grain and tone map (rav1g
  0.7.0-alpha.1+) with the same R2 threshold, so both routes write the same 8-bit codes for the same value. The
  decision is Rust (`engine_output.rs`, `stage_output_depth`), sets the fork key `bale_tonemap_output_depth=8`, and
  the guards (`FfmpegVideoOutputGuard`, `Rav1gOutputCheck`) then accept NV12 only with the tone-map mark, and marked
  P010 still (HDR passthrough, a 30+ bpp display, a wrapper that ignores the key). Needs a fork wrapper that reads
  the key; older ones keep P010.
  FFmpeg software decode outputs P010 for every source above 8 bits (fork bale.41+) and NV12 for 8-bit sources, also 4:2:2, 4:4:4, full range, RGB and grey (bale.42+).
  dav1d now hands MF CPU NV12 for 8-bit AV1 and P010 for 10-bit on D3D11 (owner, 2026-11-19);
  rav1g takes NV12 textures for 8-bit and P010 for 10-bit. Only debug `dav1d-path zerocopy` keeps
  the old D3D12 P010 pool with shifted 8-bit samples (no 10-bit processing).
  The engine's output is capped at the display's depth, per mount (`EngineOutputPolicy`, Rust
  `engine_output.rs`, passed to `bale_gpu_engine_session_open_target` as `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT`,
  logged once per open with current HDMI bpp): P010 → R10G10B10A2 for HDR passthrough (not console tone-mapped),
  or SDR output on a current HDMI mode of at least 30 bpp; otherwise B8G8R8A8. Unknown display depth treats SDR
  as 8-bit, AV1 included. NV12 (8-bit sources, 8-bit AV1 included, server transcodes, a stage on a bale_gpu without
  P010) and unknown frames always keep B8G8R8A8. Frame depth, decoders, tone map and scaling are unchanged.
  See docs/player-plan.md, "First 10-bit video after a boot slows the console GPU". The MediaPlayer fallback
  is unchanged. Debug `bale-debug.sh output-depth display|source` selects the display ceiling (default, as in
  release) or the old P010 → R10G10B10A2 behaviour; `bale-debug.sh engine-target auto|bgra|rgb10|fp16` still
  overrides everything for the next engine open. Both are memory only; `auto` (alias `default`) follows the policy.
- FFmpegInteropX packages, native activation and license texts ship in **all** builds. Keep the CI
  package checker; CsWinRT's projection alone does not register the WinRT factories for Native AOT.
- Native FFmpeg is **9.1.0-bale.dav1d.1.5.5** (stock FFmpeg n9.1, no source patches, dav1d 1.5.5; release tag
  `native-ffmpeg-9.1-dav1d-1.5.5`), built by Bale: upstream's NuGet packages stop at 8.1.2 / 9.0.2-pre01.
  Keep wrapper `FFmpegInteropX.UWP.Lib` on the pinned **2.1.0-bale.N** fork (currently **2.1.0-bale.84**),
  consumed from [Zorro92/bale-ffmpeginteropx releases](https://github.com/Zorro92/bale-ffmpeginteropx/releases).
  `scripts/ffmpeg-native.json` and `scripts/ffmpeginteropx-wrapper.json` pin their release tags
  and SHA256s; run
  `scripts/restore-ffmpeg-native.ps1` before Windows restore/build. `NuGet.Config` maps both IDs only
  to the verified local feed. CI checks the actual MSIX's dav1d version and compares the wrapper DLL
  and provenance hashes with the pinned wrapper package.
  Rebuild with the manual `ffmpeg-native.yml` workflow (repository/ref/dav1d-tag inputs), preserving
  all other upstream submodule pins and FFmpeg flags: GPL/version3/encoders remain disabled;
  never enable x264/x265. FFmpeg source changes go only through reviewed patches in
  `scripts/ffmpeg-native-patches/` (named by the `patch_revision` input/pin, e.g. `p1`); the
  build script allows them to touch only `libavcodec/libdav1d.c`. Releases are immutable. See `docs/player-plan.md` for provenance and
  rebuild steps; do not overwrite assets or update a pinned hash without reviewing the new build.
- Wrapper changes go to [Zorro92/bale-ffmpeginteropx](https://github.com/Zorro92/bale-ffmpeginteropx)
  as commits; see its [`bale/README.md`](https://github.com/Zorro92/bale-ffmpeginteropx/blob/main/bale/README.md)
  for build/release steps. The fork's CI forbids IDL/project changes and checks the built winmd's public
  type names against the retained upstream package. Bale no longer carries the wrapper patch series
  or builds wrapper packages; native FFmpeg builds remain here.
- **AV1 chain (owner, 2026-11-26: rav1g is the default AV1 decoder in release builds too, since the commit that
  wrote this; debug default since da3e045):** rav1g (D3D12 compute, the fork's GPU provider, `bale_av1_decoder=1`) →
  the fork's in-open dav1d fallback (at most 1080 lines) → one same-plan dav1d remount after a rav1g failure at a known
  source height of at most 1080 (`Rav1gPolicy.RemountOnDav1d`) → the ordinary replan or server transcode. rav1g takes a
  software AV1 mount only where Bale's once-per-process check finds rav1g.dll usable (`Rav1gLibrary`, Rust
  `av1_decoder::library_status`: packaged, loads, has the 12 exports the fork resolves and interface 3, as the fork's
  loader judges it); otherwise AV1 stays on dav1d at 1080p60 exactly as before. **Claim:** with a usable library every
  build claims AV1 Main 8/10-bit up to **2160p60** at a **40 Mbps soft ceiling** (`max_bitrate_kbps` 40 000, which
  silo-server's `videoDecodeTakesSourceV3` holds the source's bitrate to; owner: tested to 4K60, reliable to about
  40–60 Mbps, real content stays under 40) beside dav1d's measured 1080p60 entry (no bitrate bound), plus the HDR AV1
  tone-map promise of rav1g's display pass (`Av1DynamicRangeMappingSupported`); Auto's display matching counts the
  same entries. dav1d (D3D11, CPU NV12/P010 buffers, dav1d CPU film grain, **max 1080p**) stays for older consoles and
  as the fallback; the measured Series/AVX2 software-device claim gate covers both entries.
  dav1d must not require D3D12: neither claims nor its default configuration register zero-copy.
  `bale_film_grain_mode=0` means dav1d applies grain; the experimental device setting opts into mode 3
  (private D3D11 staged upload + GPU grain, CPU grain on probe failure); 2 is GPU grain on debug zero-copy streams.
  `bale_dav1d_export_film_grain=1` (`ffmpeg-tune grain=off`) wins over every mode: export only, no grain applied.
  Debug `dav1d-path [d3d11|zerocopy]` is memory only, captured per attempt, default d3d11. `zerocopy`
  alone restores the old `ZeroCopyRuntime` / `bale_zc_*` D3D12 route (upload 6, GPU grain); failed
  registration logs and keeps D3D11. Leave this runtime and bale_gpu zero-copy code dormant otherwise.
  rav1g always sets `bale_av1_fallback_max_height=1080` (wrapper bale.59): a taller stream whose GPU
  open declines fails with MF_E_UNSUPPORTED_FORMAT and takes the normal failure replan. A failed active
  rav1g mount remounts once on dav1d only with a known source height of at most 1080 (never the scaled
  output height); taller/unknown sources replan directly, withdrawing software claims including 2160.
  rav1g keeps its 250 ms buffer, 8 decode-ahead frames, frame-rate-based texture cap and display pass
  (`bale_rav1g_display=1`). Every build packages the `scripts/rav1g.json` DLL (an empty pin packages none), and CI's
  `scripts/check-rav1g-package.ps1` requires it, hash-checked with its licences, in debug-server and production
  packages alike (`scripts/test-rav1g-checks.ps1` covers both). A manual CI run's `rav1g_test_*` inputs package another
  release in the debug-server package alone, never production, without editing the pin (docs/player-plan.md, "rav1g",
  "Test build"). **Prewarm (every build, owner 2026-11-26):** the Detail
  page starts rav1g's device and pipeline compiles (`bale_rav1g_prewarm`, ~6.5 s on a Series X) when its selected or
  Auto version will play on rav1g (`Rav1gPolicy.PrewarmCandidate`, Rust `av1_decoder::prewarm_candidate`): once per
  process, on the thread pool, never at app start or on Home, never while a playback runs (`Rav1gPrewarm`). Debug-only:
  `av1-decoder dav1d|rav1g` (rav1g also prewarms at once) and `av1-claim 1080|2160|2160-dav1d` (defaults rav1g and
  2160, the release behaviour; 1080 forces 1080p60), `rav1g-tune` and the rav1g perf logs. See docs/player-plan.md, "rav1g".
- All builds tune dav1d's software AV1 with `bale_decode_ahead_frames=16` and a 1000 ms
  MSS BufferTime (the rav1g route sets its own 8 frames and 250 ms, `Rav1gPolicy.Route`). Above 1080p
  `SoftwareDecodeTuning` trims decode-ahead to 8 and BufferTime to 500 ms
  (~460 MiB less at 4K, console-measured) and sets `bale_dav1d_max_frame_delay=6` (width > 1920 or
  height > 1080), using route dimensions from the source for original delivery or the effective
  recipe with source fallback otherwise. Unknown size leaves delay unset; hardware/system video
  gets none of these keys. This tuning does not expand the measured software-video capability bounds.
- **Software video (owner, 2026-11-17, as AetherEngine; narrowed 2026-11-24):** Bale declares the software-only
  codecs its FFmpeg build decodes (`SoftwareDecodePolicy.VideoCodecs`): a software `video_decode` entry each,
  bounded only at 60 fps (owner: app-mode output is at most 60 Hz), except AV1, which keeps its own bounds
  (dav1d's measured 1080p60, and rav1g's 2160p60 at 40 Mbps where its library is usable). The route (`route::hardware_admits`) plays a file on the console's hardware decoder when a probed
  hardware entry takes its bit depth and size and the profile is 4:2:0 H.264 up to High / HEVC Main(10) / VP9 profile
  0/2; otherwise the route is software. Packages (progressive, HLS) still carry only the codecs verified there
  (`PackagedVideoCodecs`). Auto's display matching counts a version only where the console plays it (hardware entries for profiles the hardware decodes,
  bounded software entries, the fallback via `fallback_admits`), so it never prefers a software 4K decode.
  `SoftwareDecodePolicy.VideoCodecs` now means the software-only codecs (no hardware decoder). Video
  outside the FFmpeg list goes to server transcode. Measured AV1 evidence: 8/10-bit, **1080p60** (0.1.230 at 24 fps,
  0.1.439 at 59.94 fps), dav1d's entry; since 2026-11-26 every build adds rav1g's **2160p60** entry at 40 Mbps where
  rav1g.dll is usable (owner: tested to 4K60 on the console).
  MPEG-4 Part 2 and every other non-AV1 software codec use FFmpegInteropX's ordinary software output,
  never AV1-specific grain options or D3D11VA. MPEG-4 measurements (owner's Series X, 30 s each, 0 late frames):
  512×384 (0.06 cores), 720×480 (0.08), 1920×1080 (0.30); its claim is unbounded since 2026-11-17. Software video remains limited to the measured Series/AVX2
  devices; software AV1 and MPEG-4 need no zero-copy registration. AVI is an FFmpeg-only original
  container; neither AVI nor MPEG-4 is offered over progressive/HLS. `WithoutFallbacks` withdraws
  both; a failed FFmpeg software mount withdraws software codecs and AVI on the single replan.
- **Software fallback of the hardware codecs (phases A and B, owner 2026-11-24, docs/software-decode.md):** H.264, HEVC
  and VP9 the console's decoder can't take decode in FFmpeg software inside console-measured envelopes, claimed as
  bounded software entries (`software_decode::FALLBACK_ENVELOPES`: at 1920x1080 H.264 `high 10` 8/10-bit and HEVC `rext`
  12-bit up to 60 fps, H.264 `high 4:2:2`/`high 4:4:4 predictive` 8/10-bit, HEVC `rext` 8/10/12-bit and VP9 `profile
  1`/`profile 3` up to 30 fps, no bitrate bound; and 8-bit H.264 `constrained baseline`/`baseline`/`main`/`high` up to
  3840x2160, 30 fps and 50 000 kbps; ffprobe's lower-cased profile names). The server can't hold files to them where the
  profile-blind hardware entry admits the file anyway (8/10-bit HEVC RExt and VP9 profile 1/3 up to the probed size at
  60 fps, 8-bit H.264 4:2:2/4:4:4 up to 1080p60), nor see chroma (`rext` 12-bit is 4:2:0 and 4:2:2), so the route
  decides each mount (`route.software_fallback` → `FfmpegSource`): one of the attempt's current software entries must
  admit the delivered file's catalog version (`software_decode::fallback_admits` over `formatting::decodes`), a file not
  known to be 4:2:0 stays at or under 30 fps, no DV base layer is involved, and a stage the route needs runs on software
  frames (phase B, wrapper bale.80+, `software_stage`: the tone map for HDR on an SDR display; the downscale for a
  larger frame, required above 1080p unless the display is known to fit it, since Media Foundation must never scale
  those down); everything else is refused ("The video codec cannot use the local fallback"). On a software route
  FfmpegSource keeps FFmpeg software forced with the stage's keys, and the decoder check and `FfmpegVideoOutputGuard`
  expect the software decoder's stream. The 4K entry is claimed only where the stage takes software frames
  (`SoftwareDecodePolicy.ForSoftwareStage`), and display matching never counts it. HDR files inside a software envelope
  (10/12-bit, any chroma, PQ/HLG, never DV) are promised the console tone map
  (`DeviceCapabilities.SoftwareDynamicRangeMappingSupported`, `tone_map::can_manage`). VC-1/MPEG-2 have no fallback. A
  failed software route withdraws every software entry on the single replan, and for a hardware codec the codec itself
  (a remux of the same stream would fail the same way), so the server transcodes; a failed tone-map route withdraws only
  the promise. AV1 and the software-only codecs never take this stage: AV1's tone map and downscale are the rav1g route's
  own (rav1g's display pass, or the fork's stage on rav1g's frames; the fork's in-open dav1d fallback runs the stage when
  rav1g's stage keys are set), and dav1d's AV1 route sends no stage key. Debug `ffmpeg-tune
  swfallback=on` decodes past the envelope for measurements, through the route's stage.
- Hardware video claims (H.264, HEVC, VP9, VC-1, MPEG-2) follow the console's own decoder report, not hand-set
  bounds. `HardwareDecodeProbe` asks the D3D11 video device once per process, off the UI thread,
  and `ConsoleEvidence.ForHardware` claims the largest of 1080p/4K/8K each profile accepts at
  60 fps with no bitrate bound. A failed or empty probe keeps the fixed H.264/HEVC 1080p baseline
  with no VP9. Never run D3D12 video queries in production: they removed the device. Display fit
  belongs to Auto's version choice, never to a smaller claim. VP9/VC-1/MPEG-2 direct play always takes the
  FFmpeg demuxer with the system decoder and is never offered over HLS.
- **Player:** IMFMediaEngine (`Services/EngineBackend.cs`) is the default player since 2026-11-17 (owner), behind
  `PlaybackBackend`; Windows MediaPlayer (`MediaPlayerBackend`) remains only as the per-attempt fallback when the engine
  cannot open a stream, and via the debug `player-backend mediaplayer` switch. New player features target the backend
  interface, not MediaPlayer.
- `PlaybackRoutePolicy` decides each mount. HLS always plays through the Rust HLS reader and FFmpeg; Windows'
  AdaptiveMediaSource was removed 2026-11-17 (owner: it cannot feed IMFMediaEngine). A mount whose segments to the
  playlist's end hold no media (Silo's answer past a transcode's real end, server `4bee8f8`) ends playback as `MediaEnded`
  does (`EndAtEmptyTail`), never a replan. Through a server outage FFmpeg plays the reader's queued buffer while the
  reader retries a failed request, or a body cut mid-way, with the web's reconnect backoff (server `40c093c`,
  `outage_budget.rs`); the stream faults only once that budget is spent. Direct AV1/MPEG-4 Part 2
  forces FFmpeg software video; audio-only fallback forces the system video decoder and validates
  reported engines before playback. Direct MKV/WebM/AVI uses the FFmpeg demuxer with system
  video (Windows' MKV source over-reads ~2.2x and stalls on sparse subtitle tracks); MP4 stays on
  Windows' source (measured equal on both). AAC/MP3 use the pinned winmd's system audio switches;
  AC3/EAC3 reach the system Dolby decoder undecoded via patch 0021 (`bale_system_decoder_dolby`,
  keeps E-AC-3 JOC/Atmos). Passthrough remains unclaimed.
- `server_remux_progressive` (enabled since 2026-11-14) is one forward-only fMP4 response: always
  FFmpeg's non-seekable input (`ProgressiveHttpStream` → `ForwardInputStream` →
  `CreateFromInputStreamAsync`), never Windows' sources, which need a length and ranges. Never send a
  Range on it. Its reads must never throw (the wrapper's `InputStreamRead` has no exception
  handling): failures end the data and set `Faulted`. It mounts at player 0 (its keyframe origin),
  and every seek, reconnect and 60 s idle resume is a `seek_reanchor`; a progressive mount failure
  replans without progressive. Claims: `mp4`, no VP9/VC-1/MPEG-2, MP4-muxable audio only,
  `progressive_range_refusal_v1`. See docs/player-plan.md, "Progressive remux".
- Subtitles are drawn by Bale's own overlay (`bale_gpu.dll`), never by FFmpegInteropX cues. Text
  tracks are fetched sidecars (`PlaybackSubtitleResolver.Resolve`). SubRip/WebVTT are drawn in the profile's
  `playback.subtitle_appearance` (Settings › Subtitles; Core `SubtitleTextStyle` → `bale_sub_overlay_load_text_styled`,
  subtitle ABI 4); ASS keeps its authored styles and image subtitles their pixels, as on Android TV. PGS on HLS/progressive uses
  lossless `.sup` windows (`windowed=1`, source PTS), decoded in Rust into the same bitmap overlay.
  Claim `embedded_bitmap` only on these packaged deliveries; the server gates it to embedded PGS.
  Do not claim `sidecar_bitmap`: external bitmaps, DVD/DVB sidecars have no server representation.
  Image tracks (PGS/DVD/DVB) on original_http still come from direct MKV: `ResolveEmbedded` gives
  the stream index, the player binds
  the overlay to a sink token and passes `bale_subtitle_stream` / `bale_subtitle_sink` to the FFmpeg
  source (wrapper patch 0022). The claim is `native_embedded` on `original_http` alone; `Clear` does
  not unbind, so every new mount calls `BindBitmaps(0)` first. A native selection the mount cannot
  draw (invalid plan, non-FFmpeg route, no overlay, failed bind, stream missing from FFmpeg's
  `SubtitleStreams`) sends `subtitle_embedded_failed` on the single recovery replan before Play,
  keeping session, position, tracks and every claim (`PlaybackEmbeddedSubtitleFailure`). Never for a
  sidecar, packaged artifact, burn-in or a `.sup` window fetch; with the budget spent, play on without subtitles.
  In-band CEA-608 closed captions (A/53 `cc_data` in H.264/HEVC SEI and MPEG-2 user data) are decoded by bale_gpu
  `subs::cc` from the fork's packet tap (`bale_cc_sink`, caption ABI `bale_cc_*`) into a live track on the same overlay
  (`ClosedCaptions`, `SubtitleOverlay.AttachCaptions`). Every FFmpeg mount of those codecs gets its own decoder and
  token; the subtitle menus list a row for each channel seen, CC1 to CC4 (a lone CC1 stays one "CC" row), and picking
  one is client-only (the mount's decoder switches to that channel; no replan, nothing remembered, a server subtitle
  turned Off first) and survives remounts on its channel (`PlaybackClosedCaptions`, `PlayerPage.Captions.cs`,
  docs/closed-captions.md "In the player"); console check pending.
- FFmpeg audio selection must honor the server's ordinal, not FFmpeg's reordered default:
  map `AudioStreams[].StreamIndex` back to demux order, then set the item's AudioTracks.SelectedIndex
  before Play. Fail closed if the selected stream is missing.
- Display HDR is detected from HDMI modes on the UI thread at player start, including debug
  FFmpeg overrides. The bare baseline is unknown, not a hardcoded SDR display. Claim only HDR10
  from non-stereo PQ modes; native HDR10+/HLG/DV output remains unverified. Owner (2026-11-17):
  HLG must play natively when the TV supports it (as on Apple TV); today only HLG→SDR is done. On a known SDR
  display, eligible selected HEVC/VP9 10-bit PQ/HLG originals use the wrapper's d3d11va + bale_gpu
  BT.2390 stage by default (`client_managed_dynamic_range_v1` on original_http only). Require
  runtime ABI availability and explicit supported colour facts (BT.2020 NCL, or BT.709 for PQ);
  DV 8.1/8.4 and profile 7 use their compatible base layer. DV5, AV1 and unknown facts/displays
  stay server tone-mapped. Never send the claim without selected-version facts or allow alternate
  versions under it. A declined stage, decoder fallback or apply failure uses the single recovery
  replan with the range claim withdrawn, keeping working codecs. Native container sources must
  never present an HDR original on SDR. HDR displays keep native output: direct HDR10 switches
  HDMI to PQ before mounting; restore the default on stop/error, recovery, unload/navigation and suspend.
  A failed switch uses the single replan with all HDR claims withdrawn, not a codec withdrawal.
  Keep the manifest's `hevcPlayback` capability (required for Xbox HDR10/4K, with media memory
  and app/game concurrency implications). Never select 120 Hz for media; use at most 60 Hz.
- An FFmpeg create/open/MediaFailed failure gets the existing single replan with its triggering
  codec claims removed and an explicit server-transcode reason; never retry the same capability.
- **No locking FFmpegInteropX calls on the UI thread during playback (every route, 2026-11-18).** These
  `FFmpegMediaSource` members take the source lock, which the sample thread can hold while it waits on a
  decoder: `CurrentVideoStream`, `CurrentAudioStream`, `MetadataTags`, `PlaybackItem`, `PlaybackSession`
  (setter), `GetMediaStreamSource`, `CreateMediaPlaybackItem`, `StartBuffering`, `ExtractThumbnail`, the
  filter setters and `Close`/`Dispose` (the fork's bale/README.md, "rav1g prewarm and threading notes"; with
  rav1g the UI thread froze in `CurrentVideoStream` and the start budget and debug server stopped with it).
  Call them at open, before `player.Source` (no sample thread yet), on the sample thread (`SampleRequested`
  observers), or on the thread pool. The HUD, stats, `where` and `DebugVideoMediaType` read the mount's
  `FfmpegStreams` snapshot (`FfmpegStreamSnapshot`, Core `BackgroundSnapshot`: read at open, refreshed on the
  pool, one read in flight, never awaited by the UI tick); `DisposeMount` closes the native sources on the pool
  in the old order, and the next mount and the page's idle trim await that close (bounded, never blocking).
  `FfmpegUiThreadTests` pins this; docs/player-plan.md, "rav1g".
- **Start budget (silo-apple `b9446d5d`, owner 2026-11-18):** every mount, first plan and each replacement, on every
  route (engine and MediaPlayer, FFmpeg and Windows' reader, HLS through the Rust reader, progressive), shows its first
  frame within 45 s (`PlaybackStartBudget`, Rust `start_budget.rs`), held while the device is offline and deferred while
  a seek, track change or replan owns the mount. When it runs out, ask `BufferingQualityController.StartDeadlinePassed`
  first (its lowering owns the recovery; a refused or failed lowering hands the held failure back); otherwise stop the
  open (cancel the mount, close its transports, no engine→MediaPlayer fallback) and send the single failure replan as
  `noPlayableTrackWithinBudget`, keeping every claim (only progressive is withdrawn). Never fire it twice, after a first
  frame, a queued failure, Back or suspension. Debug: `bale-debug.sh start-budget`. docs/player-plan.md, "Start budget".
- DV7 originals declare only `client_dv7_to_hdr10` on `original_http`, with
  `client_video_transformations_v1`. They require FFmpeg HEVC/d3d11va BL-only decoding, never
  the native container source or system decoder. Select the first playable video in demux order;
  reject an incomplete inventory or an EL default before mounting. The live output guard must
  verify that BL stream plus P010 PQ BT.2020 on HDR, or tone-mapped (marked) P010 BT.709 on SDR. A failure
  withdraws the recipe and client-managed range claim on the existing single replan, keeping
  codec evidence. Never declare DV7→DV8.1 or Dolby Vision output. See docs/player-plan.md.
- **Owner's Dolby Vision rule (2026-11-17).** The Dolby Vision layer is proprietary: it is passed
  through untouched or dropped, never processed or tone-mapped by Bale (like Atmos/TrueHD audio).
  Dolby Vision display: pass Dolby Vision through (Windows' own source and DV path, not the FFmpeg
  base-layer route). On Xbox that is player-led/low-latency DV: the console runs Dolby's licensed
  processing and needs a TV whose HDMI mode reports `IsDolbyVisionLowLatencySupported`, requested
  with `HdmiDisplayHdrOption.DolbyVisionLowLatency`; Xbox does not map DV for non-DV TVs. HDR-only display: play only the HDR base layer / fallback. SDR display: tone-map
  from the HDR base layer, never the Dolby Vision layer. No usable base layer (profile 5) on a non-DV
  display: server. Dolby Vision output is declared only once passthrough is implemented and verified
  on a Dolby Vision display; until then the line above stands.
- Debug real-player override is **policy** (default) or **off** only; off also withdraws fallback
  claims. The standalone `playtest-ffmpeg` probe retains its experimental modes. See docs/player-plan.md.
- Never print media URLs, stream grants, headers or media/session exception messages. Log only safe
  HRESULTs, IDs/codecs and fixed diagnostics; keep FFmpeg's native log provider disabled. `Services/Log.cs` runs
  every line through Core's `LogRedaction` as a safety net, not a licence: credentials are masked in every build, and
  production builds also drop URLs (scheme kept), query strings, IPv4 and host:port addresses and the server's
  problem detail (`LogRedactionTests`).

## Player skip transport

- While the player is showing, **LT / keyboard J skips back 10 s**, **RT / keyboard L skips forward
  30 s**. Test with `scripts/bale-debug.sh key j`, `key l`, `press lt` and `press rt`. Both real analog
  trigger injection and keyboards go through the root preview-key path. Only the first DOWN acts;
  repeats and key UP are consumed, with no hold-to-scrub (`silo-android androidTvApp/.../player/TvPlayerRemoteKeyAction.kt`).
- Intervals are `TvPlayerScreen.kt`'s `LEGACY_TV_VIDEO_SEEK_INTERVALS`, also the defaults in
  `shared/.../model/settings/SeekIntervals.kt`: 10 back / 30 forward. Clamp to 0 / known movie
  duration. Rapid presses accumulate from the last requested source target, not a stale player clock.
- Skips and `bale-debug.sh seek` share `PlayerPage.Seek.cs`'s absolute-source path. A local seek waits
  for `SeekCompleted`; a target outside a produced/reanchored HLS window, and every progressive-remux
  seek, uses negotiated protocol-v3 `seek_reanchor`, retaining the same route, plan, session and tracks. This does not spend failure
  recovery budget. Seek completion flushes the actual landed source position through the sequenced
  progress reporter/journal, including short clamped seeks; pause state is preserved. A seek made while a quality or track
  change replan holds the player waits for it instead of being refused: the replacement mounts at the newest target when
  it can, else the seek follows it (silo-server `4a73f48`, `d22e55f`; PlayerPage.Seek.cs `TakeReplanSeek`).
- **Seek watchdog (AetherEngine seek budgets, 2026-11-18):** a local seek (native or FFmpeg, never a reanchor) is watched
  until `SeekCompleted` or the clock near the target, past the skip's 10 s wait. After 15 s (`PlaybackSeekWatchdog`, Rust
  `seek_watchdog.rs`, `PlayerPage.SeekWatchdog.cs`) the attempt's first stuck seek remounts the same plan at the target
  (its own start budget bounds it); a later one fails through the single replan as `softwarePipelineFailed` /
  `nativeItemFailed`, keeping every claim. A newer seek restarts it; Back, suspension and replacement mounts drop it; the
  check never calls a locking FFmpegInteropX member. Debug: `bale-debug.sh seek-watchdog hang=once`. docs/player-plan.md,
  "Seek watchdog".
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
  probes use the release ahead=16 default and D3D11 CPU output/dav1d CPU grain; URI probes have unknown dimensions before creation, so delay stays
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
  the standalone probe. Production uses the release defaults without debug overrides. `threads=0..16|default`
  sets FFmpeg's software decoder threads (`MaxDecoderThreads`, software video only; 0/default = the logical CPU
  count). `swfallback=on|off|default` decodes H.264/HEVC/VP9 the console's decoder can't take (the route's own
  software decision) in FFmpeg software even outside the release envelope (4K60, 4:2:2 at 60 fps), which release
  refuses, through the route's console stage (wrapper bale.80+); `decoder=software` takes them too but drops the stage.
  See docs/software-decode.md.

- Debug `bale-debug.sh frame-log on` (next attempt, memory only) writes one bale.log line per video sample
  (`frame: t= pts= dur= pos= lead= kind= flags=`) and per engine event (`frame-event: t= kind= pos=`): `t` is
  QueryPerformanceCounter ms, pts/dur/pos 100 ns ticks, so they join with the fork's `Bale rav1g stage` lines on pts
  and time. Debug bale.log moves to bale.1.log at 1 MiB (`xbox-logs.sh N bale.1.log`). docs/player-plan.md, "Frame log".

- `scripts/bale-debug.sh stats` prints all current player rows as `Label: value`, marking overlay-hidden
  rows `[hidden]`; no active player exits non-zero. `stats-rows [list|hide <labels>|show <labels>|only
  <labels>|reset]` filters only the overlay using one comma-separated argument of exact, case-insensitive
  labels (for example, `stats-rows hide "Read-ahead,Stream reads,Dropped (engine)"`). Labels outside the
  known overlay rows are accepted and reported as unknown; `only` also hides future unlisted rows.
  Changes re-render a visible panel immediately, also work before a player opens, and reset on app
  restart; `where` keeps every row. All rows are visible by default; `reset` restores that default.
- **Memory ceiling (owner, 2026-11-18): treat about 1.2 GB as Bale's real limit on the Xbox.** `AppMemoryUsageLimit`
  (the `mem` "of 3328.0 MiB") is not the headroom: in practice apps never get past ~1.2 GB. Judge every reading against
  that; at or above ~1.1 GB is at the ceiling (4K AV1 through rav1g read 1.25 GiB and is being trimmed).
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
  a later observer compares each sample's PTS with the player clock at delivery (`MediaPlaybackSession.Position`
  on MediaPlayer, IMFMediaEngine's current time on the engine, attached once its session is open).
  The count is per mount, with a rolling last-10-wall-second count/rate. Preroll, paused/buffering
  delivery, discontinuities and seeks are excluded. **Delivered ahead** is the last decoded sample
  end minus the current player clock, floored at zero; it is not a renderer queue depth.
- **Judge playback smoothness from stalls + drops, never dropped frames alone.** IMFMediaEngine pauses its clock
  while waiting for data and can report zero drops throughout visibly stuttering playback. All builds show
  **Missed frames** = engine drops + stalled seconds × stream fps, as a percentage of frames expected over
  playing + stalled time. The timestamped native engine-event feed counts waiting/buffering after Playing;
  initial/resume/seek preroll, user pauses and seeking do not count. Totals survive replacement mounts in the
  same playback and reset for the next playback. Network rebuffering counts too.
- Debug stats / `stats` add **Stalls** (count, total/longest ms, last-10-s count/ms, network/decoder breakdown),
  **Dropped (engine)** (cumulative despite MF counter resets), **Decode rate** (rav1g delivery fps / stream fps;
  dav1d unavailable), **Decoder discarded** (rav1g close report), **Starved** (decode-ahead timing), and
  **Playback time** (elapsed wall seconds / media advanced since first Playing, excluding seek jumps). Each completed
  stall logs its heuristic cause; source teardown logs a summary after the decoder's final counters arrive.
  Network evidence is an empty unfinished progressive/HLS reader or range demand waits/time growing between
  state samples; otherwise the tag is decoder. It is a heuristic, not proof of the bottleneck. Details:
  `docs/player-plan.md`, “Playback smoothness”. No frame textures, per-frame callbacks or frame histories.
- UWP `MediaPlayer` / `MediaPlaybackSession` exposes no renderer-drop statistics or decoded queue. Its fallback
  route shows Missed frames and Dropped (engine) as unavailable; stalls remain measurable. Do not infer renderer
  drops from buffering, clock polling, expected fps or the 23.976-on-59.94 display cadence. Missed frames
  explicitly includes stall equivalents and is separate from the measured engine drops.

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
- **`StackPanel.Spacing` still counts collapsed children**: a gap is added after every child, hidden
  or not, so an icon whose label shows only on focus sat 8 px (one hidden label) to 16 px (label and
  value) left of its round button's centre (Detail actions, player buttons, fixed 2026-11-16). For
  content with children you collapse, zero the spacing while they are hidden
  (`ActionButton.SetLabelSpacing`) or use a margin on the child instead of `Spacing`, since a
  collapsed child's margin takes no room.
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
