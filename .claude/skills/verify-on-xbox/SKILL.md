---
name: verify-on-xbox
description: Build Bale in CI, install it on the Xbox, drive it with real controller input and screenshots, and check the result against Silo's official Android TV / Apple TV apps. Use after any change to the Xbox app's UI or behavior, or when the user reports something looks or behaves wrong on the console.
---

# Verify on Xbox

Bale's Xbox app only builds on Windows (GitHub Actions), so verification happens on the real console.
Never call a UI or behavior change done from a green build alone.

Needs `XBOX_HOST` (the console IP; ask the user if unset), `gh` signed in, and a development build
(the debug server on port 8790 is compiled in by default).

## 1. Know what "right" is before looking

Find how the official client does it, so the check has a target:

- Behavior and values: `Silo-Server/silo-android`, `androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/...`
  (`gh api repos/Silo-Server/silo-android/contents/<path> --jq .content | base64 -d`).
- Design intent and focus model: `Silo-Server/silo-apple` `docs/skyline-design-guide.md`, `docs/tvos-focus.md`.
- Reference screenshots: `silo-android/ui-validation/`, `silo-apple/docs/superpowers/reference/tvos-detail/`.

Write down the expected result in one or two lines (e.g. "Down from the top bar returns to the
last-focused card, TvMainShell focusRestorer").

## 2. Build and install

```sh
export PATH=$HOME/.dotnet:$PATH
dotnet test tests/Bale.Core.Tests            # Core must pass first
git push                                     # CI builds the MSIX (~2 min)
RID=$(gh run list --limit 1 --json databaseId -q '.[0].databaseId')
gh run watch "$RID" --exit-status >/dev/null; gh run view "$RID" --json conclusion -q .conclusion
gh run view "$RID" --log-failed | grep -E ' error ' | sed 's/.*Z //' | sort -u   # if it failed
./scripts/deploy-xbox.sh                     # installs the newest build and launches it
```

Wait ~15 s for launch, then confirm it's alive: `./scripts/bale-debug.sh focus`.
If the debug port doesn't answer, check `./scripts/xbox-logs.sh` and that `Bale.exe` is running
(`curl -sk https://$XBOX_HOST:11443/api/resourcemanager/processes`); a fresh `deploy-xbox.sh` has
restored the port before.

## 3. Rules while driving (learned the hard way)

The console runs the user's real account against their real Silo server.

- **Be deliberate about writes** to the user's library (watchlist, favorite, watched, ratings,
  progress). Testing them is fine and has found real bugs, but prefer the least destructive one
  (watchlist add then remove), say what you're about to change, and undo it afterwards. Know that
  marking a series watched and then unwatched wipes its real per-episode history.
- **Don't press A in the profile menu by accident.** Check `focus` before every A: if it says
  `SwitchProfileRow`, `Sign Out` or anything in `ProfilePanel` and that isn't the test, back out with B.
- **Check focus after each step** instead of firing long press sequences blind; one surprise
  (a panel opening, the on-screen keyboard) turns the rest of the sequence into random input.
- **The on-screen keyboard captures the d-pad.** After `type`, press B once to close it, then
  check `focus` before moving on. B on the sign-in screen exits the app.
- If the app ends up somewhere unexpected (signed out, exited), stop and tell the user rather
  than trying to put it back.

## 4. Drive the exact scenario

Reproduce what the user did, or the interaction you changed, with real input:

```sh
./scripts/bale-debug.sh press down down right up   # each press prints what took focus
./scripts/bale-debug.sh key escape                 # keyboard
./scripts/bale-debug.sh type dune
./scripts/bale-debug.sh log                        # live log while you drive (Ctrl+C)
```

Print every step; don't collapse output with `uniq`/`tail`, which has hidden the real sequence before.
Go one step past the edge the user mentions (past the last card, Up from the first row, Back
from Home): that's where the bugs have been.

## 5. Look at it

```sh
./scripts/xbox-screenshot.sh /tmp/shot.png && magick /tmp/shot.png -resize 960x540 /tmp/shot-small.png
```

Read the image and compare it with the official reference: sizes, shapes (pills not ovals),
focus ring fully visible, what moved and what didn't.

## 6. If it crashed or misbehaved

- `./scripts/xbox-logs.sh 80` for errors; the file log survives crashes, the live log doesn't.
- Nothing logged means a native failure: add `Log.Info` breadcrumbs around the suspect step,
  redeploy, and read which breadcrumb was last.
- Check AGENTS.md's Native AOT pitfalls first; most console-only failures so far were one of those.

## 7. Report

Say what you expected (and from which official file), what you pressed, what happened, and attach
or describe the screenshot. If anything is unverified, say so.
