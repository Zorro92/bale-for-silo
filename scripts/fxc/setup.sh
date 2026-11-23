# Sourced by scripts/fxc-wine.sh and scripts/check-fxc.sh. bale_fxc_setup makes sure the pinned fxc.exe
# (scripts/fxc-tool.json) is extracted and the Wine image (scripts/fxc/Containerfile) is built, then exports
# BALE_FXC_DIR (directory holding fxc.exe + d3dcompiler_47.dll) and BALE_FXC_IMAGE. Returns 2 on a setup problem.
#
# The tool comes from Microsoft's official NuGet package (api.nuget.org flat container), SHA256-checked, and is
# used only as a build tool under the Windows SDK licence; its binaries are never committed. Cache:
# ${BALE_FXC_CACHE:-$HOME/.cache/bale-fxc} (~165 MB: the .nupkg is kept, so a re-extract needs no download).

bale_fxc_setup() {
  local scripts pin cache package version sha id url dir nupkg stamp tag image free_kb real
  scripts=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
  pin=$scripts/fxc-tool.json
  if [[ -n "${BALE_FXC_DIR-}" && -n "${BALE_FXC_IMAGE-}" ]]; then return 0; fi
  command -v podman >/dev/null || { echo "fxc: podman is not installed" >&2; return 2; }
  command -v jq >/dev/null || { echo "fxc: jq is not installed" >&2; return 2; }
  package=$(jq -r .package "$pin"); version=$(jq -r .version "$pin"); sha=$(jq -r .sha256 "$pin")
  cache=${BALE_FXC_CACHE:-$HOME/.cache/bale-fxc}
  # A symlinked cache (e.g. onto another drive) is used only while its target's parent exists, so a detached
  # drive never gets files written into its empty mount point.
  if [[ -L "$cache" ]]; then
    real=$(readlink -m -- "$cache")
    [[ -d "$real" ]] || { [[ -d "$(dirname -- "$real")" ]] && mkdir -p -- "$real"; } ||
      { echo "fxc: cache link $cache points at a missing location" >&2; return 2; }
  fi
  dir=$cache/$version
  stamp=$dir/x64/.verified
  if [[ "$(cat "$stamp" 2>/dev/null)" != "$sha" ]]; then
    mkdir -p -- "$dir" || return 2
    id=${package,,}
    nupkg=$dir/$id.$version.nupkg
    if [[ ! -f "$nupkg" ]] || ! echo "$sha  $nupkg" | sha256sum -c --quiet - >/dev/null 2>&1; then
      url=https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg
      echo "fxc: downloading $package $version (~160 MB, once)..." >&2
      curl -fsSL --retry 3 -o "$nupkg.part" "$url" || { echo "fxc: download failed: $url" >&2; rm -f -- "$nupkg.part"; return 2; }
      mv -f -- "$nupkg.part" "$nupkg"
    fi
    echo "$sha  $nupkg" | sha256sum -c --quiet - ||
      { echo "fxc: SHA256 mismatch for $nupkg (pin in scripts/fxc-tool.json)" >&2; return 2; }
    rm -rf -- "$dir/x64"
    mkdir -p -- "$dir/x64"
    # shellcheck disable=SC2046 # word splitting of the pinned member list is intended
    unzip -q -j -o "$nupkg" $(jq -r '.files[]' "$pin") -d "$dir/x64" || { echo "fxc: extracting $nupkg failed" >&2; return 2; }
    [[ -f "$dir/x64/fxc.exe" ]] || { echo "fxc: fxc.exe missing from $nupkg" >&2; return 2; }
    echo "$sha" >"$stamp"
  fi

  # The image tag follows the Containerfile, so editing it builds a new image (and drops the old tags).
  tag=$(sha256sum "$scripts/fxc/Containerfile" | cut -c1-12)
  image=localhost/bale-fxc:$tag
  if ! podman image exists "$image"; then
    free_kb=$(df -Pk "$HOME" | awk 'NR == 2 { print $4 }')
    if (( free_kb < 4 * 1024 * 1024 )); then
      echo "fxc: under 4 GB free on the home disk (podman storage); not building $image (~420 MB)" >&2
      return 2
    fi
    echo "fxc: building $image (Wine, once, ~2 min)..." >&2
    podman build -q -t "$image" "$scripts/fxc" >/dev/null || { echo "fxc: building $image failed" >&2; return 2; }
    podman images --format '{{.Repository}}:{{.Tag}}' localhost/bale-fxc | grep -vxF "$image" | xargs -r podman rmi >/dev/null 2>&1 || true
  fi
  export BALE_FXC_DIR=$dir/x64 BALE_FXC_IMAGE=$image
}
