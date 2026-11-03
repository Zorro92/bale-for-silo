#!/usr/bin/env bash
# Drives and inspects Bale on the console through its development debug server (Services/DebugServer.cs).
# Requires XBOX_HOST to be set to the console host.
#
#   scripts/bale-debug.sh log                   follow the live log (Ctrl+C to stop)
#   scripts/bale-debug.sh press down a          press buttons in order: up down left right a b menu view lb rb lt rt
#   scripts/bale-debug.sh key enter             keystrokes by name: enter escape tab down back f5...
#   scripts/bale-debug.sh type dune             type text as real keystrokes
#   scripts/bale-debug.sh focus                 what has focus, and the text on it
#   scripts/bale-debug.sh goto "Unwatched#2"     setup only: focus the second matching control
#   scripts/bale-debug.sh targets               list controls goto can reach
#   scripts/bale-debug.sh tree                  the visible element tree with bounds
#   scripts/bale-debug.sh fault profiles        fail the next picker profile-list load once
#   scripts/bale-debug.sh fault search          fail the next title-search request once
#   scripts/bale-debug.sh fault home            fail the next Home load once (quiet refresh keeps existing rows)
#   scripts/bale-debug.sh fault home-empty      return no rows on the next Home load once
#   scripts/bale-debug.sh fault requests        fail the next Requests hub load once
#   scripts/bale-debug.sh fault requests-empty  return no Requests rows on the next hub load once
#   scripts/bale-debug.sh fault request-detail  fail the next request-detail load once
#   scripts/bale-debug.sh show login            preview Login without changing the saved session
#   scripts/bale-debug.sh show login expired    preview the session-expired Login notice
#   scripts/bale-debug.sh show server           preview manual server entry
#   scripts/bale-debug.sh show home             return from an auth preview to the signed-in app
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
  key)
    shift
    for key in "$@"; do
      echo "$key → $(curl -fsS -X POST "$BASE/key/$key")"
    done
    ;;
  type)
    shift
    curl -fsS -X POST -G "$BASE/type" --data-urlencode "text=$*"
    ;;
  focus) curl -fsS "$BASE/focus" ;;
  goto)
    shift
    curl --fail-with-body -sS -X POST -G "$BASE/goto" --data-urlencode "target=$*"
    ;;
  targets) curl -fsS "$BASE/targets" ;;
  tree) curl -fsS "$BASE/tree" ;;
  fault)
    if [[ "$#" != 2 || ( "${2:-}" != profiles && "${2:-}" != search && "${2:-}" != home && "${2:-}" != home-empty && "${2:-}" != requests && "${2:-}" != requests-empty && "${2:-}" != request-detail ) ]]; then
      echo "usage: scripts/bale-debug.sh fault {profiles|search|home|home-empty|requests|requests-empty|request-detail}" >&2
      exit 2
    fi
    curl -fsS -X POST "$BASE/fault/$2"
    ;;
  show)
    case "${*:2}" in
      login) route=login ;;
      "login expired") route=login/expired ;;
      server) route=server ;;
      home) route=home ;;
      *)
        echo "usage: scripts/bale-debug.sh show {login|login expired|server|home}" >&2
        exit 2
        ;;
    esac
    curl --fail-with-body -sS -X POST "$BASE/show/$route"
    ;;
  *)
    sed -n '/^#/{s/^# \{0,1\}//;p;};/^set /q' "$0"
    exit 2
    ;;
esac
