#!/usr/bin/env bash
# Saves what's on the Xbox screen right now, via Device Portal.
#   XBOX_HOST=192.168.1.50 scripts/xbox-screenshot.sh [out.png]
set -euo pipefail
HOST="${XBOX_HOST:?Set XBOX_HOST to your Xbox IP (shown in Dev Home)}"
OUT="${1:-xbox-$(date +%Y%m%d-%H%M%S).png}"
AUTH=()
[[ -n "${XBOX_USER:-}" ]] && AUTH=(--user "$XBOX_USER:${XBOX_PASS:-}")
curl -fsSk "${AUTH[@]}" -o "$OUT" "https://$HOST:11443/ext/screenshot?download=true"
echo "$OUT"
