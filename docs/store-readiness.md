# Microsoft Store readiness (Xbox)

Status (2026-11-26): the package and the build are prepared for certification; the submission itself is the
owner's (checklist below). This page lists what CI checks automatically, what Partner Center asks for, the Store
policies that apply to a client for a self-hosted server, the Xbox UX guidance, licences, and the decisions still open.

Policy numbers are from the Microsoft Store Policies, version 7.20
([store-policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)). The Xbox design guidance
is Microsoft's archived "Designing for Xbox and TV"; it is guidance, not a certification policy.

## Automated checks

| What | Where | Runs |
|---|---|---|
| Manifest: Windows.Xbox only (MinVersion 10.0.19041.0, at least the 10.0.14393.0 Xbox needs), exactly internetClient, privateNetworkClientServer and hevcPlayback in the source, inputInjectionBrokered only when the transform gets `debugServer=true`, BaleDebugServer off by default, production revision 0, client channel `release` / `sideload` | `StoreManifestTests` | every push (Linux) |
| Every `src/**/*Debug*.cs` is wholly `#if DEBUG_SERVER` (no `#else`; allow-list empty) | `DebugSourceGuardTests` | every push |
| bale_gpu's debug-only exports are exactly the ones the `debug-probes` / `stage-timing` features gate, and CI's lists agree | `BaleGpuDebugExportTests` | every push |
| Log redaction (credentials always; URLs, queries, addresses, problem detail in production) | `LogRedactionTests` | every push |
| The production package scan's own fixtures, and the WACK report summary | `scripts/test-production-package-checks.ps1` | every push (Windows job) |
| Packaged bale_gpu.dll exports: common list in both builds, debug-only list only in debug-server builds | ci.yml, "Check FFmpeg native activation and package contents" | each package |
| Packaged bale_core.dll: the debug JSON calls (`media_fault.set`, `http_probe.run`, `http_probe.fetch_pattern`) answer only in debug-server builds | `scripts/check-bale-core-calls.ps1` | each package |
| The MSIX against the Store rules (below) | `scripts/check-production-package.ps1` | each package |
| Windows App Certification Kit | `scripts/run-wack.ps1` | production packages, non-blocking |

`check-production-package.ps1` fails a production package that declares any capability besides internetClient,
privateNetworkClientServer and hevcPlayback (it names inputInjectionBrokered), targets anything but Windows.Xbox alone,
has a non-zero revision, carries a `Microsoft.UI.Xaml*.dll` or a Microsoft.UI.Xaml dependency (WinUI 2 is the debug
server's WebView2 test), a `.pdb`, or any of five debug markers (the debug server routes `/media-fault`,
`/audio-out-test` and `/playtest-ffmpeg` as UTF-16 literals in `Bale.exe`; two bale_gpu debug-probe report strings as
UTF-8 in `bale_gpu.dll`), or lacks `rav1g.dll` or one of its licence files. On a debug-server package it requires the
capability and every marker instead, so each push proves the scan still sees them. It prints a note while a production
package has the development identity.

### Run a production build with WACK

Production packages are built only on request (owner: no routine production builds):

```sh
gh workflow run ci.yml --ref main -f production=true
gh run list --workflow ci.yml --limit 3            # the new run's id
gh run watch <run-id> --exit-status                # or wait however you prefer
gh run download <run-id> -n bale-xbox-x64-production -D /tmp/bale-production
gh run download <run-id> -n wack-report -D /tmp/bale-wack
```

- The `Xbox package (Windows, debug=false)` job's log has "Check the package against the Store rules" (PASS or the
  failing rule) and "Windows App Certification Kit (production)": the overall result and every test that did not
  pass, with its messages, or one line saying why WACK did not run.
- The `wack-report` artifact holds `wack-report.xml` and appcert's own logs. The kit writes XML; its HTML view comes
  from the interactive kit. Microsoft's docs both say the command line "must be run within the context of an active
  user session" and that it "can now be integrated into an automated testing where no interactive user session is
  available"; on a hosted runner the step stops appcert after 10 minutes and says so, and never fails the job.
- WACK on a desktop runner cannot install an Xbox-only package, so expect the static tests only; Microsoft's docs note
  that apps not targeting desktop may see "Not all tests were run during validation". Partner Center runs its own
  certification on submission.
- Versions: production is `0.1.<run>.0` (the Store reserves the fourth part). Each submission must be higher than the
  last, so a Store upload comes from a new run, never "Re-run jobs" (same run number). Debug-server builds stay
  `0.1.<run>.<attempt - 1>` for sideload updates.
- Identity: the Store needs the identity Partner Center reserves. Set the repository variables
  `BALE_STORE_IDENTITY_NAME`, `BALE_STORE_PUBLISHER` (the `CN=...` value) and `BALE_STORE_PUBLISHER_DISPLAY_NAME` from
  Partner Center › Product management › Product identity. Production builds then stamp them, and CI's throwaway
  certificate takes that Publisher as its subject; debug-server builds keep `Bale.ClientForSilo` / `CN=Bale
  Development`, so both install side by side. These values are public, not secrets.

## Partner Center: capability justifications

- **hevcPlayback** (restricted): required on Xbox for HEVC and 4K/HDR10 video through the system decoders (Microsoft's
  "Xbox HEVC/4K" guidance). Bale is a video player for the user's own Silo media server; without it HEVC and 4K HDR
  playback is unavailable. It also opts the app into the media memory budget.
- **privateNetworkClientServer**: reaching Silo servers on the user's home network (LAN addresses), and Silo's LAN
  server discovery on the sign-in screen.
- **internetClient**: reaching Silo servers over the internet, their artwork and metadata, and media streams.

The debug server's `rescap:inputInjectionBrokered` is not in production packages (CI refuses it).

## Store policies that apply

- **10.2.4, dependency on another service.** Bale is useless without a Silo server. Disclose that at the start of the
  Store description, for example "Bale is a client for Silo, a self-hosted media server; you need a Silo server to use
  it." (Silo's trademark policy: never name the app "Silo ...".)
- **10.3, testable product.** 10.3.1 asks for a working demo account and 10.3.2 a functional server. Use the public
  Silo demo server (address and login in the owner's submission notes, "Notes for certification"). Never write the
  address or the login in this repository. Say what to try: sign in, pick a profile, play a title.
- **10.4.2, starts promptly and stays responsive.** Every playback mount has a 45 s start budget (`PlaybackStartBudget`)
  and the seek watchdog; launch navigates straight to the first page and loads from the network asynchronously. Check
  it in the reviewer run below.
  One known slow path: the first rav1g (AV1) open after launch compiles rav1g's pipelines (~6.5 s on a Series X)
  because the prewarm is debug-only (docs/player-plan.md, "rav1g").
- **10.5.1, privacy policy.** Bale sends the server's credentials, viewing progress and device identity (device name,
  model, a random device id, client name, version and channel) to the Silo server the user chooses, and nothing to any
  other party. A privacy policy URL is still needed in Partner Center. To do: a privacy row in Settings that shows or
  links it.
- **10.5.4, secure transport.** Server setup tries HTTPS first (Auto order: HTTPS, then HTTP, then HTTP on 8090, as
  silo-android's ServerSetupAddress.kt). Plain HTTP needs the user's explicit consent per exact origin, after a warning
  ("Your password and what you watch will be sent unencrypted to {host}. Only do this on a network you trust."),
  stored as a digest (`ServerSetupAddress.OriginDigest`). The consent is not limited to private addresses today; see
  the decisions below.
- **10.13.4, Xbox.** Bale never asks for or stores Microsoft account credentials (Silo accounts only, in the app's
  PasswordVault), sells nothing, and has no general file, storage or media browsing: only the server's catalog.
- **11.1 and 11.11, age rating.** Complete the IARC questionnaire in Partner Center. The app ships no media; all
  content comes from the user's own server after sign-in with a pre-existing account, the opt-in 11.11.3 names, and
  the server's profiles (with PINs) and its own controls apply.
- **10.2.5.** The production package installs and updates only through the Store (no self-update, no downloaded code).

## Xbox UX checklist

From the archived "Designing for Xbox and TV" and "Gamepad and remote control interactions". Bale's UI follows
silo-android's TV app (AGENTS.md); where that differs from Microsoft's guidance, the owner decides.

- **Controller-only, mouse mode off.** `App.xaml.cs` sets `RequiresPointerMode = WhenRequested` and no page requests the
  pointer, so the gamepad cursor never appears. Check: every screen works with the controller alone.
- **XY focus, no traps.** The root frame enables `XYFocusKeyboardNavigation`; rails, grids and pages use the shared
  focus building blocks (AGENTS.md: `Rail`, `FocusRetry`, `IRestoresFocus`). Back always leaves a screen
  (`IBackHandler`). Check on the console: from any screen, Back reaches Home, and no direction gets stuck.
- **Title-safe area.** Bale draws edge to edge (`SetDesiredBoundsMode(UseCoreWindow)`), so text and interactive
  elements must stay 48 epx from the left and right and 27 epx from the top and bottom. Nothing checks this
  automatically: screenshot each screen on the console (`xbox-screenshot.sh`) and look at the edges, the player's
  controls and the Options panel included.
- **SystemMediaTransportControls.** The engine player registers with SMTC (`EngineSystemControls`): play, pause, next /
  fast-forward and previous / rewind from media remotes and the system overlay, with the playback status kept current.
- **Text and control sizes.** Microsoft suggests at least 15 epx for main text, 12 epx for non-critical text and 32 epx
  high interactive elements. Bale maps Android's sizes (text `sp × 0.86`), so some captions and badges are smaller:
  for example 9-9.9 epx episode captions on Detail, 7.74 epx badges and the skip icons' 6.5 epx numerals; tabs are
  30 epx high, buttons 32. A Store requirement only in the general 10.4 sense; see the decisions below.
- **Prompt start, suspend and resume.** Launch shows the first page without waiting on the network. Entering the
  background or suspending releases UI art and suspends playback within the deadline (`App.OnSuspending`,
  `OnEnteredBackground`, `UiResidency`), restores the HDMI mode, and resume reloads lazily. Check: launch a game
  during playback, come back, and play again.
- **Memory.** The 1 GB app limit in Microsoft's 2017 guidance is out of date for this app type: Bale measured
  3328 MiB of app memory (one pool for CPU and GPU, ≤ 2857 MiB GPU-resident) on a Series X with hevcPlayback
  (docs/resource-budget.md, docs/gpu-tone-map.md "Measured limits"). Older consoles may differ; the owner treats about
  1.2 GB as the practical ceiling (AGENTS.md).

## Licences

- `THIRD_PARTY_NOTICES.md` lists every bundled component; on 2026-11-26 it gained the HTTPS stack bale_core.dll links
  in every build (ureq, rustls, ring, rustls-webpki, untrusted, subtle, webpki-roots and their helper crates) with the
  ISC, BSD-3-Clause and CDLA-Permissive-2.0 texts.
- FFmpeg is the LGPL-2.1-or-later build (no GPL, no version3, no encoders) shipped as separate DLLs, with corresponding
  source and provenance in its native release and `Licenses/FFmpeg/`.
- rav1g's licences (BSD-2-Clause, the AOM patent licence, rav1d's and the Rust crates') ship as `Licenses/rav1g/`, and
  the production check requires them.
- Bale is AGPL-3.0-or-later. Partner Center lets a publisher supply its own licence terms instead of the Store's
  standard application licence; with copyleft components (Bale's AGPL, FFmpeg's LGPL) the owner should decide whether to
  do so, and say where the corresponding source is.
- To do: the app does not show any of this. Add an About / Legal row in Settings with the version, the licence, the
  source link and the notices; `THIRD_PARTY_NOTICES.md` itself is not packaged today (only the `Licenses/` texts are).

## Owner checklist for submission

1. Reserve the app name in Partner Center (not "Silo ..."), set the three `BALE_STORE_*` repository variables from
   Product identity, and fill in the age rating (IARC), privacy policy URL, description (Silo dependency first),
   screenshots and the "Notes for certification" with the demo server and login.
2. Justify the capabilities with the texts above (hevcPlayback is restricted and needs it).
3. Dispatch a production build (`gh workflow run ci.yml --ref main -f production=true`); confirm the Store rules check
   passed and read the WACK summary.
4. Reviewer-style run on a retail-like console: install the production package with
   `XBOX_HOST=... scripts/deploy-xbox.sh /tmp/bale-production` (with the Store identity it installs beside the debug
   build; with the development identity, uninstall the debug build first, since both can be version 0.1.<run>.0), then
   with the controller only: add the demo server, sign in, pick a profile, browse, play something (one HEVC/4K title
   and one AV1 title if the demo has them), seek, open Options, press Back to Home, suspend by launching a game,
   resume, play again. Check the title-safe edges on screenshots.
5. Check `bale.log` from that run (`scripts/xbox-logs.sh`): no addresses, tokens or server problem text.
6. Upload the production `.msix` from the same run.

## Decisions for the owner

- **Stats tab.** The player's Options › Stats shows technical rows in production too (Backend, Route, Video and Audio
  decoder labels, and others; `PlaybackStats`). Hide them in production, or keep them as the official apps' "stats for
  nerds" equivalent?
- **The EXPERIMENTAL Settings group** (GPU film grain for the AV1 fallback, off by default). Ship it, hide it in
  production, or rename the group?
- **The debug server** is unauthenticated and listens on the whole LAN (port 8790) with input injection. Debug-server
  builds must never be installed on someone else's console; production has none of it (CI checks).
- **Plain HTTP to non-local servers.** Allowed today after the per-origin warning, as in the official clients. Restrict
  consent to private-network addresses in Store builds (10.5.4), or keep parity?
- **Text below Microsoft's minimums** (above): keep the Skyline sizes, or raise the smallest captions and badges.
- **Debug code still in release binaries**, not covered by this pass (no production caller; each needs its own care):
  bale_gpu's frame observer (`bale_frame_observer_*`), `bale_gpu_engine_present`, `bale_gpu_engine_session_open`,
  `_set_target`, `_volume` and `bale_sub_overlay_load_text`; bale_core's frame meters (`bale_core_frames_*`,
  `_delivery_*`, `_trace_*`, only the debug frame delivery uses them). `bale_sub_overlay_load_ass` has no caller at all.

## Not verifiable on Linux

The Windows build (XAML, Native AOT, packaging), the exports of the real Windows DLLs, the marker scan on a real MSIX,
WACK itself, and everything on the console. The first production run (and the next push, for the debug direction of the
package scan) is where those checks first meet a real package.
