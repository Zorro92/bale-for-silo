---
name: cswinrt-check
description: Run Bale's Linux CsWinRT pre-check (scripts/check-cswinrt.sh) before pushing C# changes to src/Bale.Xbox, so errors like CsWinRT1028 "class must be partial" show up in about 15 s instead of failing CI's Windows MSIX build. Use whenever you add or change C# types in the Xbox app, especially classes that implement IDisposable or other WinRT-projected interfaces, nested classes, or anything passed to WinRT APIs.
---

# CsWinRT pre-check (Linux)

The Xbox app (`src/Bale.Xbox`) can't be packaged on Linux: the UWP XAML compiler, `cswinrt.exe`, the MSIX
steps and native AOT are Windows-only. The C# compile and the CsWinRT analyzers do run on Linux, and those
analyzers raise the errors that most often break CI's Xbox package build.

## Run it

```bash
scripts/check-cswinrt.sh              # exit 0 = no CsWinRT errors, 1 = errors (printed), 2 = setup failed
scripts/check-cswinrt.sh --self-test  # plants a non-partial class under DEBUG_SERVER; must report CsWinRT1028
scripts/check-cswinrt.sh --all        # also counts the other (expected) errors
```

Run it after editing C# in `src/Bale.Xbox` and before `git push`; it checks the working tree, including
uncommitted and untracked files. If it prints a CsWinRT error, fix it (usually: mark the class *and every
containing class* `partial`) and run it again. Run `--self-test` once if the toolchain, CsWinRT version or the
script changed, to prove the check still sees errors.

## What it does (and what it doesn't)

- Compiles a copy of the working tree in `~/.cache/bale-cswinrt-check` (override with `BALE_CSWINRT_CACHE`,
  keep it on a Linux filesystem, not exFAT). The real checkout is never restored or built, so `packages.lock.json` stays
  untouched (the copy restores with its lock file redirected).
- Fetches the pinned FFmpegInteropX packages (from `scripts/ffmpeginteropx-wrapper.json` and
  `scripts/ffmpeg-native.json`) into the copy's own feed, checking their SHA256.
- Drops the debug-only WinUI 2 reference and its one user (`DebugServer.WebViewTest.cs`) in the copy, so
  `DEBUG_SERVER` code is still analysed.
- Reports **only CsWinRT diagnostics**. The compile itself "fails" with thousands of CS0103 errors, because
  the XAML pass that generates `InitializeComponent` and `x:Name` fields is missing; that is expected noise.
- It does **not** catch XAML errors, ordinary C# errors in code-behind, trimming/AOT errors or packaging
  problems. CI's Windows build stays the real check; this only catches the CsWinRT class of failure early.
- Needs `dotnet`, `jq`, `rsync` and an authenticated `gh` (for the package download on first run / after a
  pin bump). First run downloads ~50 MB; later runs take ~10–35 s.
