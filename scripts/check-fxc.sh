#!/usr/bin/env bash
# FXC pre-check for the bale_gpu shaders on Linux (seconds, instead of waiting for CI's Windows build).
#
# The shaders are SM5 (ps/vs/cs_5_0, FXC, D3D11) and CI compiles them with /WX, so FXC-only diagnostics such as
# X3571 "pow(f, e) will not work for negative f" fail the Windows build. This runs Microsoft's own fxc.exe (pinned
# in scripts/fxc-tool.json) under Wine in a container (scripts/fxc-wine.sh) through native/bale-gpu/build.rs, so
# the shader list and flags stay the ones in build.rs. It also compiles the standalone SM5 tone-map math that
# scripts/test-tonemap-shaders.ps1 checks on CI. Prints only fxc's diagnostics plus a one-line verdict.
#
# Usage: scripts/check-fxc.sh [--self-test]
#   --self-test  plant a pow() of a value FXC cannot prove non-negative in a temp copy and require X3571
# Exit: 0 = all shaders compile, 1 = fxc errors, 2 = setup problem (no podman/cargo, download, SHA, image build).
# Needs podman, jq, curl, unzip and the x86_64-pc-windows-msvc Rust target. First run downloads ~160 MB and builds
# a ~420 MB image (localhost/bale-fxc:<Containerfile hash>); later runs reuse both.
set -euo pipefail
root=$(git rev-parse --show-toplevel)
export PATH="$HOME/.cargo/bin:$PATH"
self_test=0
case "${1-}" in --self-test) self_test=1 ;; "") ;; *) echo "usage: $0 [--self-test]" >&2; exit 2 ;; esac
verdict() { echo "check-fxc: $*" >&2; }

# shellcheck source=fxc/setup.sh
source "$root/scripts/fxc/setup.sh"
bale_fxc_setup || { verdict "setup failed; shaders not checked"; exit 2; }
rustup target list --installed 2>/dev/null | grep -qx x86_64-pc-windows-msvc ||
  { verdict "x86_64-pc-windows-msvc Rust target not installed (rustup target add x86_64-pc-windows-msvc)"; exit 2; }

crate=$root/native/bale-gpu
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
if (( self_test )); then
  # A copy of the crate (without target/) whose subtitle shader returns pow() of a possibly negative value.
  mkdir "$tmp/bale-gpu"
  (cd "$crate" && git ls-files -z -co --exclude-standard) | rsync -a --from0 --files-from=- "$crate/" "$tmp/bale-gpu/"
  probe=$tmp/bale-gpu/shaders/sub_ps.hlsl
  sed -i 's/return input\.color \* coverage;/return pow(input.color * (coverage * 2.0 - 1.0), 2.4);/' "$probe"
  grep -q 'return pow(' "$probe" || { verdict "self-test could not plant its probe in shaders/sub_ps.hlsl"; exit 2; }
  crate=$tmp/bale-gpu
fi

# Own target dir: FXC switches build.rs from placeholders to real blobs, which must not churn the lint cache.
# bale-gpu itself is cleaned so build.rs (and so fxc) always runs; the dependencies stay cached.
export CARGO_TARGET_DIR=$root/native/bale-gpu/target/fxc-check CARGO_INCREMENTAL=0
export FXC=$root/scripts/fxc-wine.sh
cargo clean -q -p bale-gpu --target x86_64-pc-windows-msvc --manifest-path "$crate/Cargo.toml" 2>/dev/null || true
log=$tmp/cargo.log
set +e
(cd "$crate" && cargo check -q --locked --lib --target x86_64-pc-windows-msvc) >"$log" 2>&1
rc=$?
math=$crate/tests/shaders/tonemap_math.hlsl
if [[ $rc -eq 0 && -f "$math" ]]; then
  (cd "$crate" && "$FXC" /nologo /WX /T ps_5_0 /E main /I shaders /Fo "$tmp/math-sm5.cso" tests/shaders/tonemap_math.hlsl) >>"$log" 2>&1
  rc=$?
fi
set -e

# fxc diagnostics: path(line,col): error|warning Xnnnn; paths shown relative to the crate, each line once.
real_crate=$(realpath -- "$crate")
diags=$(grep -E '\([0-9]+,[0-9-]+\): (error|warning) X[0-9]+' "$log" | sed -E "s#^[[:space:]]*##; s#^($real_crate|$crate)/#native/bale-gpu/#" | awk '!seen[$0]++' || true)
[[ -n "$diags" ]] && echo "$diags"
if (( self_test )); then
  if grep -q 'X3571' <<<"$diags"; then verdict "self-test passed (planted pow() rejected with X3571)"; exit 0; fi
  [[ $rc -eq 0 ]] && { verdict "self-test FAILED: the planted pow() compiled"; exit 1; }
  tail -20 "$log" >&2; verdict "self-test FAILED: no X3571 (exit $rc)"; exit 1
fi
if [[ $rc -eq 0 ]]; then verdict "all shaders compile with fxc (SM5, /WX)"; exit 0; fi
if [[ -n "$diags" ]] || grep -qE 'fxc failed for|compilation failed' "$log"; then
  verdict "fxc errors; the Windows build would fail"; exit 1
fi
tail -20 "$log" >&2
verdict "cargo check failed for a reason other than fxc (exit $rc); shaders not fully checked"
exit 2
