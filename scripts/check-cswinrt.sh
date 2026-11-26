#!/usr/bin/env bash
# CsWinRT pre-check for Bale.Xbox on Linux (seconds, instead of waiting for CI's Windows build).
#
# The UWP build steps (XAML compiler, cswinrt.exe projection, MSIX, native AOT) only run on Windows, but
# the C# compile and the CsWinRT analyzers run anywhere. This compiles a copy of the working tree with
# EnableWindowsTargeting and reports only CsWinRT diagnostics (e.g. CsWinRT1028 "class must be partial"),
# which the analyzers raise even though the rest of the build is incomplete. Everything else (XAML-generated
# members, so thousands of CS0103 errors, plus XAML/AOT/packaging errors) is noise here and stays with CI.
#
# Never builds the real checkout: it rsyncs tracked + untracked (not ignored) files into a cache copy,
# restores with the lock file redirected, and fetches the pinned FFmpegInteropX packages (SHA256-checked
# against scripts/ffmpeginteropx-wrapper.json and scripts/ffmpeg-native.json) into the copy's own feed.
# It compiles the debug-server build (-p:BaleDebugServer=true; the project's default is the shipping build), which
# holds nearly all code; --release compiles the shipping build instead. The debug-only WinUI 2 reference is dropped
# in the copy (its targets need Windows), so DEBUG_SERVER code is still analysed.
#
# Usage: scripts/check-cswinrt.sh [--all|--self-test|--release]
#   --all        also print the other error IDs and counts (expected noise)
#   --self-test  plant a non-partial IDisposable class in the copy and require CsWinRT1028 (proves the check works)
#   --release    analyse the shipping build (BaleDebugServer=false) instead of the debug-server build
# Cache: ${BALE_CSWINRT_CACHE:-$HOME/.cache/bale-cswinrt-check} (keep it off exFAT; ~100 MB + NuGet cache)
set -euo pipefail
root=$(git rev-parse --show-toplevel)
cache=${BALE_CSWINRT_CACHE:-$HOME/.cache/bale-cswinrt-check}
copy=$cache/src
feed=$cache/feed
show_all=0 self_test=0 debug_server=true
case "${1-}" in --all) show_all=1 ;; --self-test) self_test=1 ;; --release) debug_server=false ;; "") ;;
  *) echo "usage: $0 [--all|--self-test|--release]" >&2; exit 2 ;; esac
mkdir -p "$copy" "$feed"

# 1. Pinned packages into the private feed (download once, verify every run).
fetch() { # pin-json default-repo
  local pin=$1 repo version tag sha id file
  version=$(jq -r .version "$pin"); tag=$(jq -r .release_tag "$pin"); sha=$(jq -r .sha256 "$pin")
  repo=$(jq -r ".release_repository // \"$2\"" "$pin"); id=$3
  file=$feed/$id.$version.nupkg
  if [[ ! -f "$file" ]] || ! echo "$sha  $file" | sha256sum -c --quiet - 2>/dev/null; then
    rm -f "$feed/$id".*.nupkg
    gh release download "$tag" -R "$repo" -p "$id.$version.nupkg" -D "$feed" --clobber >/dev/null
    echo "$sha  $file" | sha256sum -c --quiet - || { echo "check-cswinrt: SHA256 mismatch for $id $version" >&2; exit 2; }
  fi
}
fetch "$root/scripts/ffmpeginteropx-wrapper.json" Zorro92/bale-ffmpeginteropx FFmpegInteropX.UWP.Lib
fetch "$root/scripts/ffmpeg-native.json" Zorro92/bale-for-silo FFmpegInteropX.UWP.FFmpeg

# 2. Working tree (including uncommitted edits) into the copy; bin/obj kept between runs for speed.
(cd "$root" && git ls-files -z -co --exclude-standard) | rsync -a --from0 --files-from=- "$root/" "$copy/"
# rsync --files-from never deletes; drop files that left the tree so stale sources don't compile.
comm -23 <(cd "$copy" && find . -type f -not -path './.cache/*' -not -path '*/bin/*' -not -path '*/obj/*' | sed 's#^\./##' | sort) \
         <(cd "$root" && git ls-files -co --exclude-standard | sort) | (cd "$copy" && xargs -r -d '\n' rm -f)
mkdir -p "$copy/.cache"
ln -sfn "$feed" "$copy/.cache/ffmpeg-native"
proj=$copy/src/Bale.Xbox/Bale.Xbox.csproj
sed -i '/Include="Microsoft.UI.Xaml"/d' "$proj"
rm -f "$copy/src/Bale.Xbox/Services/DebugServer.WebViewTest.cs" # the only WinUI 2 user
probe=$copy/src/Bale.Xbox/CsWinRtCheckProbe.cs
rm -f "$probe"
if (( self_test )); then
  printf '%s\n' '#if DEBUG_SERVER' 'namespace Bale.Xbox;' 'internal static class CsWinRtCheckProbe' '{' \
    '    internal sealed class Nested : System.IDisposable { public void Dispose() { } }' '}' '#endif' >"$probe"
fi

# 3. Compile. Exit code is meaningless (the XAML pass is missing); only CsWinRT IDs count.
log=$cache/build.log
dotnet build "$proj" --nologo -v:q -clp:NoSummary \
  -p:EnableWindowsTargeting=true -p:Platform=x64 -p:CsWinRTGenerateProjection=false -p:BaleDebugServer=$debug_server \
  -p:NuGetLockFilePath="$cache/packages.lock.json" -p:RestoreLockedMode=false \
  -p:TreatWarningsAsErrors=false -p:BaleCoreNativeLibrary= >"$log" 2>&1 || true

found=$(grep -E "(error|warning) CsWinRT[0-9]+" "$log" | sed -E "s# \[[^]]*\]\$##; s#^[^(]*/src/Bale\.Xbox/#src/Bale.Xbox/#" | sort -u || true)
if ! grep -qE "error CS[0-9]+|CsWinRT" "$log"; then
  echo "check-cswinrt: the compile did not run (restore/setup failed); see $log" >&2
  grep -E "error" "$log" | grep -vE "https?://|token|api_key" | head -10 >&2
  exit 2
fi
if (( show_all )); then
  echo "Other diagnostics (expected noise from the missing XAML pass):"
  grep -oE "error [A-Za-z]+[0-9]+" "$log" | sort | uniq -c | sort -rn | head -15
fi
if (( self_test )); then
  rm -f "$probe"
  if grep -q "CsWinRtCheckProbe\.cs.*error CsWinRT1028" <<<"$found"; then
    echo "check-cswinrt: self-test passed (planted non-partial class reported as CsWinRT1028)."; exit 0
  fi
  echo "check-cswinrt: self-test FAILED: the planted class was not reported; see $log" >&2; exit 1
fi
if [[ -n "$found" ]]; then
  echo "$found"
  echo "check-cswinrt: CsWinRT diagnostics found (CI's Xbox package build would fail on errors)." >&2
  grep -q "error CsWinRT" <<<"$found" && exit 1
  exit 0
fi
echo "check-cswinrt: no CsWinRT diagnostics."
