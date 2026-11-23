#!/usr/bin/env bash
# Prints Bale's log (LocalState/bale.log) from an Xbox in Developer Mode, via Device Portal.
#   XBOX_HOST=<console-ip> scripts/xbox-logs.sh [lines] [bale.log|bale.1.log]
# Debug builds move bale.log to bale.1.log at 1 MiB (the new file's first line says so); pass bale.1.log for the
# older part, e.g. of a frame log (bale-debug.sh frame-log) that crossed the limit.
set -euo pipefail
FILE="${2:-bale.log}"
[[ "$FILE" == bale.log || "$FILE" == bale.1.log ]] || { echo "usage: scripts/xbox-logs.sh [lines] [bale.log|bale.1.log]" >&2; exit 2; }
HOST="${XBOX_HOST:?Set XBOX_HOST to your Xbox IP (shown in Dev Home)}"
BASE="https://$HOST:11443"
AUTH=()
[[ -n "${XBOX_USER:-}" ]] && AUTH=(--user "$XBOX_USER:${XBOX_PASS:-}")

PACKAGE="$(curl -fsSk "${AUTH[@]}" "$BASE/api/app/packagemanager/packages" \
  | jq -r '[.InstalledPackages[] | select(.PackageFullName | startswith("Bale.ClientForSilo_"))]
           | max_by([.Version.Major, .Version.Minor, .Version.Build, .Version.Revision]) | .PackageFullName')"
[[ "$PACKAGE" != null ]] || { echo "Bale isn't installed on $HOST." >&2; exit 1; }

curl -fsSk "${AUTH[@]}" -G "$BASE/api/filesystem/apps/file" \
  --data-urlencode knownfolderid=LocalAppData \
  --data-urlencode "packagefullname=$PACKAGE" \
  --data-urlencode 'path=\LocalState' \
  --data-urlencode "filename=$FILE" | tail -n "${1:-200}"
