#!/usr/bin/env bash
# Push to origin main through the pre-push hook, stop at the first failure with its reason, then print the
# CI run of the pushed commit. For agents: never chain the CI wait after a push with `;`.
#   scripts/push-main.sh [refspec]       default HEAD:main
# Exit: 0 = pushed, "run <id> <sha>" printed; 1 = push or hook failed (reason printed); 2 = no CI run after 2 min.
set -uo pipefail
ref=${1:-HEAD:main}
log=$(mktemp); trap 'rm -f "$log"' EXIT
# Secrets never reach the terminal: drop URL and credential-looking lines from everything printed.
clean() { grep -vE "https?://|token|api_key" || true; }
if ! git push origin "$ref" >"$log" 2>&1; then
  echo "push-main: push failed" >&2
  clean <"$log" | grep -iE "error|fail|panicked|rejected|denied|warning:" | head -20 >&2
  echo "--- last lines ---" >&2
  clean <"$log" | tail -8 >&2
  exit 1
fi
clean <"$log" | grep -E "Passed!|check-cswinrt:|check-fxc:|recreated"
sha=$(git rev-parse HEAD)
for _ in $(seq 1 24); do
  run=$(gh run list --commit "$sha" --json databaseId -q '.[0].databaseId' 2>/dev/null)
  if [[ -n "$run" ]]; then echo "run $run ${sha:0:7}"; exit 0; fi
  sleep 5
done
echo "push-main: pushed ${sha:0:7} but no CI run appeared in 2 min (docs-only push? see paths-ignore in ci.yml)" >&2
exit 2
