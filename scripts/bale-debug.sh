#!/usr/bin/env bash
# Drives and inspects Bale on the console through its development debug server (Services/DebugServer.cs).
# Requires XBOX_HOST to be set to the console host.
#
#   scripts/bale-debug.sh log                   follow the live log (Ctrl+C to stop)
#   scripts/bale-debug.sh press down a          press buttons in order: up down left right a b menu view lb rb lt rt
#   scripts/bale-debug.sh key enter             keystrokes by name: enter escape tab down back f5...
#   scripts/bale-debug.sh type dune             type text as real keystrokes
#   scripts/bale-debug.sh focus                 what has focus, and the text on it
#   scripts/bale-debug.sh where                 page, overlays, focus, status and on-screen controls
#   scripts/bale-debug.sh wait page Detail      wait for a page name containing Detail, then print where
#   scripts/bale-debug.sh wait loaded           wait for visible loading indicators to disappear
#   scripts/bale-debug.sh wait text "Clear filters"  wait for visible text (case-insensitive)
#   scripts/bale-debug.sh wait focus Play       wait for a focused description containing Play
#   scripts/bale-debug.sh wait --timeout 12000 loaded  default 8000 ms; 0–15000; timeout prints where and fails
#   scripts/bale-debug.sh caps [section]        installed decoders, GPU decode profiles, display modes, audio mix and baseline
#                                               (section: video, gpu, audio, display, device or graph)
#   scripts/bale-debug.sh playtest <url> [seconds]  muted public http(s) stream probe (1–10 s, default 3)
#   timeout 300 scripts/bale-debug.sh playtest suite [seconds]  Chromium samples, one at a time
#                                               (the whole suite needs more than the usual timeout 20)
#   scripts/bale-debug.sh goto "Unwatched#2"     setup only: focus the second matching control
#   scripts/bale-debug.sh targets               list controls goto can reach
#   scripts/bale-debug.sh tree                  the visible element tree with bounds
#   scripts/bale-debug.sh fault profiles        fail the next picker profile-list load once
#   scripts/bale-debug.sh fault search          fail the next title-search request once
#   scripts/bale-debug.sh fault catalog         fail the next Library Browse grid page load once (Clear filters / Retry then uses the server)
#   scripts/bale-debug.sh fault home            fail the next Home load once (quiet refresh keeps existing rows)
#   scripts/bale-debug.sh fault home-empty      return no rows on the next Home load once
#   scripts/bale-debug.sh fault requests        fail the next Requests hub load once
#   scripts/bale-debug.sh fault requests-empty  return no Requests rows on the next hub load once
#   scripts/bale-debug.sh fault request-detail  fail the next request-detail load once
#   scripts/bale-debug.sh fault playback        inject a decoder failure into the player (one replan, then error)
# Connection previews use disposable fixtures: no network, passwords sent, vault or saved-list writes.
#   scripts/bale-debug.sh show login            preview phone Login (no live ticket)
#   scripts/bale-debug.sh show login expired    preview the session-expired Login notice
#   scripts/bale-debug.sh show login password   preview password fields, reveal and local validation/errors
#   scripts/bale-debug.sh show servers          preview Add / Switch / Remove on an in-memory list
#   scripts/bale-debug.sh show server           preview manual server entry and advanced options
#   scripts/bale-debug.sh show server http-warning  preview Connect / Cancel origin consent
#   scripts/bale-debug.sh show server discovery  preview two nearby rows (no LAN browsing)
# Profile previews use a fresh in-memory household: Save/Delete never change real profiles or selection.
#   scripts/bale-debug.sh show profiles manage  preview Manage/Done, Add and Delete (primary PIN 1234)
#   scripts/bale-debug.sh show profile create   preview Create Profile over the fixture picker
#   scripts/bale-debug.sh show profile edit     preview Edit Profile over the fixture picker
#   scripts/bale-debug.sh show home             return from a disposable preview to the signed-in app
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
  where) curl -fsS --max-time 5 "$BASE/where" ;;
  caps) curl -fsS --max-time 18 -G "$BASE/caps" ${2:+--data-urlencode "only=$2"} ;;
  playtest)
    playtest_seconds="${3:-3}"
    if [[ "$#" -lt 2 || "$#" -gt 3 || ! "$playtest_seconds" =~ ^([1-9]|10)$ ]]; then
      echo 'usage: scripts/bale-debug.sh playtest <http(s)-url|suite> [seconds] (1–10, default 3; suite needs timeout 300)' >&2
      exit 2
    fi
    playtest_url() {
      # 8 s to open + playback + 1 s margin; at most 19 s under the usual outer timeout 20.
      curl --fail-with-body -sS --max-time "$((playtest_seconds + 9))" -G "$BASE/playtest" \
        --data-urlencode "url=$1" --data-urlencode "seconds=$playtest_seconds"
    }
    if [[ "$2" == suite ]]; then
      suite_base='https://raw.githubusercontent.com/chromium/chromium/main/media/test/data'
      suite_status=0
      for sample in \
        bear-1280x720.mp4 \
        bear-hevc-frag.mp4 \
        bear-320x240-v_frag-hevc.mp4 \
        bear-vp9.webm \
        bear-av1.mp4 \
        bear-av1.webm \
        bear-av1-320x180-10bit.mp4 \
        bear-ac3-only-frag.mp4 \
        bear-eac3-only-frag.mp4 \
        bear-flac.mp4 \
        bear-opus.mp4; do
        printf '\n=== %s/%s ===\n' "$suite_base" "$sample"
        playtest_url "$suite_base/$sample" || suite_status=$?
      done
      exit "$suite_status"
    fi
    playtest_url "$2"
    ;;
  wait)
    shift
    wait_timeout=8000
    wait_args=()
    wait_usage='usage: scripts/bale-debug.sh wait [--timeout MS] {page <substring>|loaded|text <substring>|focus <substring>} (0–15000 ms)'
    while (( $# > 0 )); do
      if [[ "$1" == --timeout ]]; then
        if [[ "$#" -lt 2 || ! "$2" =~ ^[0-9]{1,5}$ ]]; then
          echo "$wait_usage" >&2
          exit 2
        fi
        wait_timeout=$((10#$2))
        shift 2
      else
        wait_args+=("$1")
        shift
      fi
    done
    if (( wait_timeout > 15000 )); then
      echo "$wait_usage" >&2
      exit 2
    fi
    case "${wait_args[0]:-}" in
      loaded) valid_wait=$(( ${#wait_args[@]} == 1 )) ;;
      page|text|focus)
        if [[ "${wait_args[*]:1}" == *[![:space:]]* ]]; then valid_wait=1; else valid_wait=0; fi
        ;;
      *) valid_wait=0 ;;
    esac
    if (( ! valid_wait )); then
      echo "$wait_usage" >&2
      exit 2
    fi
    # Leave room for dispatch and the summary response, still below the usual outer timeout 20.
    curl --fail-with-body -sS --max-time "$(( (wait_timeout + 999) / 1000 + 2 ))" -G "$BASE/wait" \
      --data-urlencode "for=${wait_args[*]}" --data-urlencode "timeout=$wait_timeout"
    ;;
  goto)
    shift
    curl --fail-with-body -sS -X POST -G "$BASE/goto" --data-urlencode "target=$*"
    ;;
  targets) curl -fsS "$BASE/targets" ;;
  tree) curl -fsS "$BASE/tree" ;;
  fault)
    if [[ "$#" != 2 || ( "${2:-}" != profiles && "${2:-}" != search && "${2:-}" != catalog && "${2:-}" != home && "${2:-}" != home-empty && "${2:-}" != requests && "${2:-}" != requests-empty && "${2:-}" != request-detail && "${2:-}" != playback ) ]]; then
      echo "usage: scripts/bale-debug.sh fault {profiles|search|catalog|home|home-empty|requests|requests-empty|request-detail|playback}" >&2
      exit 2
    fi
    curl -fsS -X POST "$BASE/fault/$2"
    ;;
  show)
    case "${*:2}" in
      login) route=login ;;
      "login expired") route=login/expired ;;
      "login password") route=login/password ;;
      servers) route=servers ;;
      server) route=server ;;
      "server http-warning") route=server/http-warning ;;
      "server discovery") route=server/discovery ;;
      "profiles manage") route=profiles/manage ;;
      "profile create") route=profile/create ;;
      "profile edit") route=profile/edit ;;
      home) route=home ;;
      *)
        echo "usage: scripts/bale-debug.sh show {login|login expired|login password|servers|server|server http-warning|server discovery|profiles manage|profile create|profile edit|home}" >&2
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
