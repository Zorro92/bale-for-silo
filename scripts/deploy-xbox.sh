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

# Local packages may not use the CI filename, but their manifest still identifies the build.
EXPECTED_VERSION="$(basename "$MSIX" | cut -d_ -f2)"
if [[ ! "$EXPECTED_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  EXPECTED_VERSION=""
  MANIFEST="$(unzip -p "$MSIX" AppxManifest.xml 2>/dev/null || true)"
  IDENTITY_PATTERN='<Identity[[:space:]][^>]*>'
  VERSION_PATTERN="[[:space:]]Version=[\"']([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)[\"']"
  if [[ "$MANIFEST" =~ $IDENTITY_PATTERN && "${BASH_REMATCH[0]}" =~ $VERSION_PATTERN ]]; then
    EXPECTED_VERSION="${BASH_REMATCH[1]}"
  fi
fi
if [[ -z "$EXPECTED_VERSION" ]]; then
  echo "Warning: Could not determine the MSIX version; cannot verify which Bale build was installed." >&2
fi

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

# State can report success without installing the build, and the package list can briefly
# contain both versions. Allow 20 seconds for the exact build to appear before launching it.
for (( ATTEMPT=0; ATTEMPT<=10; ATTEMPT++ )); do
  PACKAGES="$("${CURL[@]}" -f "$BASE/api/app/packagemanager/packages" \
    | jq -c '[.InstalledPackages[] | select(.PackageFullName | startswith("Bale.ClientForSilo_"))]')"
  if [[ -z "$EXPECTED_VERSION" ]]; then
    PACKAGE="$(jq -c 'max_by([.Version.Major, .Version.Minor, .Version.Build, .Version.Revision])' <<<"$PACKAGES")"
    break
  fi
  PACKAGE="$(jq -c --arg version "$EXPECTED_VERSION" \
    'map(select(([.Version.Major, .Version.Minor, .Version.Build, .Version.Revision] | join(".")) == $version)) | first' <<<"$PACKAGES")"
  [[ "$PACKAGE" != "null" ]] && break
  if (( ATTEMPT < 10 )); then sleep 2; fi
done
if [[ -n "$EXPECTED_VERSION" && "$PACKAGE" == "null" ]]; then
  VERSIONS="$(jq -r 'map([.Version.Major, .Version.Minor, .Version.Build, .Version.Revision] | join("."))
                     | unique | if length == 0 then "(none installed)" else join(", ") end' <<<"$PACKAGES")"
  echo "Install reported success, but the console still has Bale $VERSIONS, not $EXPECTED_VERSION. Re-run the deploy; if it keeps failing, restart the console." >&2
  exit 1
fi
INSTALLED_VERSION="$(jq -r '[.Version.Major, .Version.Minor, .Version.Build, .Version.Revision] | join(".")' <<<"$PACKAGE")"
echo "Installed Bale $INSTALLED_VERSION."

b64uri() { printf %s "$1" | base64 -w0 | jq -sRr @uri; }
APPID="$(b64uri "$(jq -r .PackageRelativeId <<<"$PACKAGE")")"
FULLNAME="$(b64uri "$(jq -r .PackageFullName <<<"$PACKAGE")")"

"${CURL[@]}" -f -o /dev/null --data '' -H "X-CSRF-Token: $CSRF" \
  "$BASE/api/taskmanager/app?appid=$APPID&package=$FULLNAME"
echo "Launched Bale $INSTALLED_VERSION."
