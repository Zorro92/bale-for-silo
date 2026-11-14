---
name: playback-test-on-xbox
description: Run a playback or picture test on the Xbox with Bale's debug switches - pick a clip with the right codec/HDR range, force a route (native, FFmpeg, HLS transcode), pause on the same frame across routes, take clean screenshots and measure them. Use for HDR/tone-mapping comparisons, route A/B tests, decoder checks, or any "play X on route Y and compare" task. For UI/focus checks use verify-on-xbox instead.
---

# Playback test on Xbox

Companion to `verify-on-xbox` (build, install, safety rules). Everything here was hit for real.
Needs `XBOX_HOST`, a development build (debug server on 8790) and the `Bale Test` profile.

## 0. Before touching the console

- The console is shared with the rav1g project. Check its T3 thread isn't running a console test,
  and that no other player is running:
  `curl -sk https://$XBOX_HOST:11443/api/resourcemanager/processes` (look for `uwp_dx12_player.exe`).
  If the user said "only when I say it's free", wait for that.
- `./scripts/deploy-xbox.sh` installs the CI build of HEAD (waits for CI). Pull first.
- Profile picker: use **Bale Test** only. Never Sign out / Change server.

## 1. Pick the clip with the CLI, not by guessing

```sh
C=tools/Bale.Cli/bin/Debug/net10.0/bale-cli; export BALE_PROFILE="Bale Test"
$C search "<title>"                      # ids
$C detail <id>                           # versions: "1080P · HDR · HEVC · MKV"
$C play-plan <id> [--file <fileId>]      # source line: codec, profile, bits, range=hdr10|hdr10_plus|dv..., audio
```

- Detail's `HDR` badge covers HDR10, HDR10+ and more; only `play-plan`'s `range=` tells them apart.
- The audio codec decides the route as much as the video does (e.g. DTS → FFmpeg software audio).
- Filter all CLI output: `| grep -vE "https?://|token|api_key"`.
- Never put titles in commits or docs; describe the clip (codec, range, resolution) instead.

## 2. Arm the debug switches (memory only, next playback attempt)

```sh
scripts/bale-debug.sh hdr pretend|off          # pretend = claim HDR10, skip HDMI switch (console tone map)
scripts/bale-debug.sh ffmpeg policy|direct|off # off = native MediaPlayer route (MKV goes FFmpeg by default)
scripts/bale-debug.sh ffmpeg-tune hdr=on|off|default
scripts/bale-debug.sh delivery hls|auto        # force HLS
```

Set them **before** pressing Play; changing them mid-play doesn't affect the running attempt.
Always reset to defaults when done: `hdr off`, `ffmpeg policy`, `ffmpeg-tune hdr=default`, `delivery auto`.

## 3. Navigate by content id for playback tests

Use the CLI ids from step 1 and start with a clean navigation stack:

```sh
scripts/bale-debug.sh home
scripts/bale-debug.sh open <content-id> [file=<fileId>]
scripts/bale-debug.sh play <content-id> [file=<fileId>] [start=<seconds>]
scripts/bale-debug.sh navlog [N]             # last 1–200 navigation/input/player events, default 50
```

`home` opens Browse/Home for the current profile and clears Back/Forward history. It fails if no
profile is selected; select **Bale Test** deliberately, never let a test pick a household profile.
`open` waits for Detail to load, selects the exact file using Version-picker state, and focuses Play.
`play <id>` uses that Detail's normal Play path (claims/planning/debug switches included), then waits
for a ready player or error and prints the player state and plan delivery. `start=` is an exact
non-negative source time without the normal resume rewind; omitted uses normal Play/resume behavior.
`play` with no id still resumes the mounted player after a paused seek.
Open/Home allow 30 seconds and Play allows 90 seconds; allow a larger outer tool timeout too.
HTTP 404 (unknown content/file), 408 (timeout) and 409 (no profile/conflict) fail non-zero.

Prefer `home` + `open`/`play` over Search/`goto` for playback setup. Use real `key`/`press` only for
behavior being tested. If keyboard navigation is itself under test, remember:

- The **first `type` after opening Search can be lost**, and old query text can remain and get
  appended. Check `where` instead of assuming a new query replaced the previous one.
- **Card text changes with debug switches, including `hdr pretend`**. A prior `goto "<card text>"`
  may stop matching. Inspect `targets`; chain any subsequent Enter with `&&`.
- **A mismatched `goto` + Enter can start unintended playback** from whatever still has focus.
  Never send Enter after a failed `goto`.
- **Back follows the page stack**: it can return to an old Detail page, not Home.
  `key back` is page Back, not backspace; use `home` for a known clean starting point.
- **The first Enter on Detail after leaving the player is often swallowed** (`key` returns 408).
  Check `where`/the result before taking any further action; direct `play <id>` avoids that key handoff.
- After playback the button can become `Resume mm:ss`; specify `start=` when the source start matters.
- **After two navigation failures in a row, stop and use `home`/`open` instead of retrying keys.**

Diagnose with `navlog` (page types, safe route ids, input results and player open/stop events with
timestamps). Every `bale-debug.sh` invocation also appends timestamp, subcommand/args, HTTP status,
and the first response line to `${XDG_STATE_HOME:-$HOME/.local/state}/bale-debug/commands.log`
(override with `BALE_DEBUG_COMMAND_LOG`). URLs/credentials and typed text are redacted. HTTP
404/408/409 warnings go to stderr even if stdout is redirected; keep stderr in test reports.

## 4. Confirm the route before measuring anything

```sh
scripts/bale-debug.sh wait --timeout 15000 loaded
scripts/bale-debug.sh stats | grep -E "Backend|Route|Delivery|HDR output|FFmpeg HDR|media type|codec"
```

`Video media type (track|mss)` shows the colour/HDR attributes MF actually got (transfer,
primaries, matrix, range, MaxCLL/MaxFALL, mastering). On the FFmpeg route, HEVC/VP9/VC-1/MPEG-2
go compressed to the system decoder and FFmpegInteropX attaches **no** colour attributes, so
`ffmpeg-tune hdr=` changes nothing for them (it only affects FFmpeg-decoded video, i.e. AV1).

## 5. Same frame on every route

- Direct routes snap seeks to keyframes: `seek 600 pause` landed on 595.25. Re-use the **landed**
  time (`seek 595.25 pause`) for every other case; it is then exact and repeatable.
- HLS transcodes have their own timeline: the same source time can be a few frames off, even a
  different shot at a cut. Step `seek <t> pause` in small increments (0.1–0.5 s) and pick the
  closest by RMSE: `magick compare -metric RMSE ref.png cand.png null:`.
- Paused seeks do redraw on every route. If the frame looks wrong, it's the timeline, not a stale frame.
- Use `scripts/bale-debug.sh play` to resume after `seek <t> pause`; `pause` pauses again and
  `toggle` switches between them. No playback restart or controller input is needed.
- Pick a frame with what the test needs (skin + a highlight for tone mapping). Contact sheets help:
  `magick a.png b.png c.png -resize 640x360 +append sheet.png`.

## 6. Clean screenshots

- **The stats overlay (`key i`) persists across playbacks.** Before each screenshot check
  `where | grep -o "Stats: visible=[a-z]*"` and toggle it off. A stats-covered shot measures as
  nonsense (p50 ≈ 22, max 237).
- `scripts/xbox-screenshot.sh out.png` (Device Portal) captures the composed SDR output, video
  included. Identical pipelines give pixel-identical shots: `magick compare -metric AE a.png b.png null:`
  returning 0 is a real result, not a capture failure.

## 7. Measuring

No PIL/numpy here; use ffmpeg raw output with plain Python (`python3 -I`). Crop the letterbox
first (2.39:1 at 1080p: `crop=1920:780:0:150`). Report luma min/p1/p50/p99/max on 0–255
(BT.709 weights on the RGB values) and mean HSV saturation. A sketch:

```sh
ffmpeg -v error -i shot.png -vf crop=1920:780:0:150 -f rawvideo -pix_fmt rgb24 - | python3 -I -c '...'
```

Rough reading for HDR→SDR: max ~150 with p99 ~130 and low saturation = PQ code values shown
unconverted (≈200-nit PQ ≈ code 150); black ~16 / white ~235 = range mismatch; reaches ~220+ with
normal saturation = real tone map.

## 8. Waiting

Foreground `sleep` is blocked. Use `scripts/bale-debug.sh wait ...` with a condition (`loaded`,
`page X`, `focus X`) and a timeout; don't poll in loops.

## 9. Report

For each case: route, delivery, the media-type line, landed time, screenshot path, the numbers,
and which pattern it matches. Say what couldn't be observed (e.g. the system decoder's output type
is not visible from the app).
