#!/usr/bin/env bash
# Prints Bale's log (LocalState/bale.log) from an Xbox in Developer Mode, via Device Portal.
#   XBOX_HOST=192.168.1.50 scripts/xbox-logs.sh [lines]
set -euo pipefail
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
  --data-urlencode filename=bale.log | tail -n "${1:-200}"
