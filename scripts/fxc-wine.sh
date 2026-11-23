#!/usr/bin/env bash
# Drop-in fxc.exe for Linux: `FXC=scripts/fxc-wine.sh cargo check ...` makes native/bale-gpu/build.rs compile the
# real shaders. Runs Microsoft's pinned fxc.exe under Wine in the localhost/bale-fxc container (scripts/fxc/), with
# fxc's own arguments, exit code, stdout and stderr; absolute Linux paths in the arguments become Z:\ paths, and
# Z:\ paths in fxc's output become Linux paths again. Retries a run that hangs or dies in Wine startup.
# Normally driven by scripts/check-fxc.sh; exit 2 = setup problem (no podman, download or image build failed).
set -uo pipefail
# shellcheck source=fxc/setup.sh
source "$(dirname "${BASH_SOURCE[0]}")/fxc/setup.sh"
bale_fxc_setup || exit 2

declare -A mounts=()
cwd=$(pwd -P)
mounts[$cwd]=1
args=()
for arg in "$@"; do
  # An absolute path has a second slash; fxc's own flags (/T, /Fo, /nologo) do not.
  if [[ "$arg" == /*/* ]]; then
    real=$(realpath -m -- "$arg")
    parent=$(dirname -- "$real")
    [[ -d "$parent" ]] && mounts[$parent]=1
    [[ -d "$real" ]] && mounts[$real]=1
    arg="Z:${real//\//\\}"
  fi
  args+=("$arg")
done
volumes=()
for dir in "${!mounts[@]}"; do volumes+=(-v "$dir:$dir"); done

out=$(mktemp) err=$(mktemp)
trap 'rm -f "$out" "$err"' EXIT
for attempt in 1 2 3; do
  name=bale-fxc-$$-$attempt
  # cgroups disabled: no systemd scope per run (a busy user manager once stalled every container start).
  timeout -s KILL 120 podman run --rm --name "$name" --network=none --cgroups=disabled \
    --security-opt label=disable -v "$BALE_FXC_DIR:/opt/fxc:ro" "${volumes[@]}" -w "$cwd" "$BALE_FXC_IMAGE" \
    timeout -s KILL 90 wine /opt/fxc/fxc.exe "${args[@]}" >"$out" 2>"$err"
  rc=$?
  [[ $rc -eq 137 ]] && podman rm -f "$name" >/dev/null 2>&1
  # 0 = compiled, 1 with fxc's own verdict = shader errors; anything else is Wine/podman trouble.
  if [[ $rc -eq 0 ]] || { [[ $rc -eq 1 ]] && grep -qE 'error X[0-9]+|compilation failed' "$out" "$err"; }; then
    break
  fi
  echo "fxc-wine: attempt $attempt failed (exit $rc)$([[ $attempt -lt 3 ]] && echo ', retrying')" >&2
done
# Z:\a\b\c.hlsli(95,34-77) -> /a/b/c.hlsli(95,34-77)
unwine() { perl -pe 's{Z:(\\[^\s():]*)}{$1 =~ tr|\\\\|/|r}ge'; }
unwine <"$out"
unwine <"$err" >&2
exit "$rc"
