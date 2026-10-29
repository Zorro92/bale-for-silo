#!/usr/bin/env bash
# Drives and inspects Bale on the console through its development debug server (Services/DebugServer.cs).
#
#   XBOX_HOST=192.168.1.50 scripts/bale-debug.sh log            follow the live log (Ctrl+C to stop)
#   XBOX_HOST=192.168.1.50 scripts/bale-debug.sh press down a   press buttons in order: up down left right a b menu view
#   XBOX_HOST=192.168.1.50 scripts/bale-debug.sh type dune      type into the focused text box
#   XBOX_HOST=192.168.1.50 scripts/bale-debug.sh focus          what has focus, and the text on it
#   XBOX_HOST=192.168.1.50 scripts/bale-debug.sh tree           the visible element tree with bounds
set -euo pipefail
HOST="${XBOX_HOST:?Set XBOX_HOST to your Xbox IP (shown in Dev Home)}"
BASE="http://$HOST:8790"

case "${1:-}" in
  log)
    since=0
    while true; do
      reply="$(curl -fsS --max-time 5 "$BASE/log?since=$since" || true)"
      if [[ -n "$reply" ]]; then
        since="$(head -n1 <<<"$reply")"
        tail -n +2 <<<"$reply" | sed '/^$/d'
      fi
      sleep 0.5
    done
    ;;
  press)
    shift
    for button in "$@"; do
      echo "$button → $(curl -fsS -X POST "$BASE/press/$button")"
    done
    ;;
  type)
    shift
    curl -fsS -X POST -G "$BASE/type" --data-urlencode "text=$*"
    ;;
  focus) curl -fsS "$BASE/focus" ;;
  tree) curl -fsS "$BASE/tree" ;;
  *)
    sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
