#!/usr/bin/env bash
# Regenerates src/Bale.Core/Api from Silo's published OpenAPI document.
# Bump SILO_REF to pick up a newer server contract, then commit the result.
set -euo pipefail

SILO_REF="${SILO_REF:-cc8514943289045e7b9f7ac8d36d4968bfe93926}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SPEC_DIR="$ROOT/.cache"
SPEC="$SPEC_DIR/silo-openapi-$SILO_REF.json"

mkdir -p "$SPEC_DIR"
if [[ ! -f "$SPEC" ]]; then
  curl -fsSL -o "$SPEC" \
    "https://raw.githubusercontent.com/Silo-Server/silo-server/$SILO_REF/contracts/api/v2/openapi.json"
fi

# Only the endpoints Bale uses today; add --include-path entries as features land.
cd "$ROOT"
dotnet tool restore >/dev/null
dotnet kiota generate \
  --language CSharp \
  --openapi "$SPEC" \
  --class-name SiloApiClient \
  --namespace-name Bale.Core.Api \
  --output src/Bale.Core/Api \
  --exclude-backward-compatible \
  --clean-output \
  --log-level Warning \
  --include-path '/api/v2/auth/device/start' \
  --include-path '/api/v2/auth/device/poll' \
  --include-path '/api/v2/auth/device/cancel' \
  --include-path '/api/v2/auth/device/capability' \
  --include-path '/api/v2/auth/refresh' \
  --include-path '/api/v2/auth/logout' \
  --include-path '/api/v2/account/me' \
  --include-path '/api/v2/profiles' \
  --include-path '/api/v2/profiles/{id}/verify-pin' \
  --include-path '/api/v2/home/capabilities' \
  --include-path '/api/v2/home/layout' \
  --include-path '/api/v2/home/sections' \
  --include-path '/api/v2/home/sections/{id}/items' \
  --include-path '/api/v2/catalog/items/{id}' \
  --include-path '/api/v2/catalog' \
  --include-path '/api/v2/user/libraries' \
  --include-path '/api/v2/library/{id}/sections' \
  --include-path '/api/v2/catalog/series/{id}/seasons' \
  --include-path '/api/v2/catalog/series/{id}/seasons/{num}/episodes' \
  --include-path '/api/v2/favorites/{item_id}' \
  --include-path '/api/v2/watchlist/{item_id}' \
  --include-path '/api/v2/watched/{id}'

echo "Generated client from Silo-Server/silo-server@$SILO_REF"
