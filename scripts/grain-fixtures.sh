#!/usr/bin/env bash
# Regenerate synthetic AV1 film-grain oracles. CI only reads the committed files.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
exec python3 -I "$root/scripts/grain-fixtures.py" "$root/native/bale-gpu/tests/fixtures/grain" "$@"
