#!/usr/bin/env bash
# Installs a Bale build on an Xbox in Developer Mode through Windows Device Portal, then launches it.
#
#   XBOX_HOST=192.168.1.50 scripts/deploy-xbox.sh            newest successful CI build on main
#   XBOX_HOST=192.168.1.50 scripts/deploy-xbox.sh <dir>      a package folder you already have
#
# Set XBOX_USER and XBOX_PASS too if Device Portal authentication is turned on in Dev Home.
set -euo pipefail

HOST="${XBOX_HOST:?Set XBOX_HOST to your Xbox IP (shown in Dev Home)}"
BASE="https://$HOST:11443"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DIR="${1:-}"
if [[ -z "$DIR" ]]; then
  RUN_ID="$(gh run list --workflow ci.yml --branch main --status success --limit 1 --json databaseId -q '.[0].databaseId')"
  [[ -n "$RUN_ID" ]] || { echo "No successful CI build on main yet." >&2; exit 1; }
  DIR="$ROOT/.cache/build-$RUN_ID"
  if [[ ! -d "$DIR" ]]; then
    echo "Downloading CI build $RUN_ID..."
    gh run download "$RUN_ID" --name bale-xbox-x64 --dir "$DIR"
  fi
fi

MSIX="$(find "$DIR" -name '*.msix' -not -path '*/Dependencies/*' | head -n1)"
CER="$(find "$DIR" -name '*.cer' | head -n1)"
mapfile -t DEPS < <(find "$DIR" -path '*/Dependencies/x64/*' \( -name '*.appx' -o -name '*.msix' \))
[[ -n "$MSIX" ]] || { echo "No .msix found in $DIR" >&2; exit 1; }

JAR="$WORK/cookies"
CURL=(curl -sSk --cookie-jar "$JAR" --cookie "$JAR")
[[ -n "${XBOX_USER:-}" ]] && CURL+=(--user "$XBOX_USER:${XBOX_PASS:-}")

# Device Portal wants its CSRF cookie echoed back as a header on every POST.
"${CURL[@]}" -f -o /dev/null "$BASE/api/os/info"
CSRF="$(awk '$6 == "CSRF-Token" { print $7 }' "$JAR")"

FORM=(-F "package=@$MSIX")
[[ -n "$CER" ]] && FORM+=(-F "certificate=@$CER")
for i in "${!DEPS[@]}"; do FORM+=(-F "dependency$i=@${DEPS[$i]}"); done

echo "Installing $(basename "$MSIX") on $HOST..."
"${CURL[@]}" -f -o /dev/null -H "X-CSRF-Token: $CSRF" "${FORM[@]}" \
  "$BASE/api/app/packagemanager/package?package=$(basename "$MSIX")"

# 204 while installing, 200 when done, 404 until the install has registered (allow a few),
# anything else is a failure with a JSON reason.
NOT_STARTED=0
while true; do
  STATUS="$("${CURL[@]}" -o "$WORK/state" -w '%{http_code}' "$BASE/api/app/packagemanager/state")"
  case "$STATUS" in
    204) sleep 2 ;;
    404) (( ++NOT_STARTED < 10 )) || { echo "Install never started: $(cat "$WORK/state")" >&2; exit 1; }; sleep 1 ;;
    200) break ;;
    *) echo "Install failed ($STATUS): $(cat "$WORK/state")" >&2; exit 1 ;;
  esac
done
echo "Installed."

PACKAGE="$("${CURL[@]}" -f "$BASE/api/app/packagemanager/packages" \
  | jq -c '.InstalledPackages[] | select(.PackageFullName | startswith("Bale.ClientForSilo_"))')"
b64uri() { printf %s "$1" | base64 -w0 | jq -sRr @uri; }
APPID="$(b64uri "$(jq -r .PackageRelativeId <<<"$PACKAGE")")"
FULLNAME="$(b64uri "$(jq -r .PackageFullName <<<"$PACKAGE")")"

"${CURL[@]}" -f -o /dev/null --data '' -H "X-CSRF-Token: $CSRF" \
  "$BASE/api/taskmanager/app?appid=$APPID&package=$FULLNAME"
echo "Launched Bale $(jq -r .Version.Major,.Version.Minor,.Version.Build <<<"$PACKAGE" | paste -sd.)."
