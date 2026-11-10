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
#   scripts/bale-debug.sh stats                 all current player rows as Label: value, including overlay-hidden rows marked [hidden]
#   scripts/bale-debug.sh frame-trace [reset]   last 300 software-video late deliveries: stream waits, GC pauses, delivery gaps or other; reset clears trace/totals
#   scripts/bale-debug.sh threads [seconds]     per-thread CPU over 1–30 s (default 10), in cores, grouped by thread name or start module; plus whole process
#   scripts/bale-debug.sh stats-rows [list|hide <labels>|show <labels>|only <labels>|reset]
#                                               list (default) shows each current label on/off; changes affect only the overlay, immediately
#                                               labels are one comma-separated argument, exact and case-insensitive; unknown labels are allowed and reported
#                                               example: stats-rows hide "Read-ahead,Stream reads,Dropped frames"; only also hides future unlisted rows; Dropped frames is hidden by default
#                                               memory only, resets on app restart; works before a player opens; stats/where always keep every row
#   scripts/bale-debug.sh wait page Detail      wait for a page name containing Detail, then print where
#   scripts/bale-debug.sh wait loaded           wait for visible loading indicators to disappear
#   scripts/bale-debug.sh wait text "Clear filters"  wait for visible text (case-insensitive)
#   scripts/bale-debug.sh wait focus Play       wait for a focused description containing Play
#   scripts/bale-debug.sh wait --timeout 12000 loaded  default 8000 ms; 0–15000; timeout prints where and fails
#   scripts/bale-debug.sh ffmpeg <mode>         next real-player attempt: policy (default)|direct|off; memory only, HLS unchanged
#                                               direct: every direct mount demuxes with FFmpeg, video still system (hardware) decoded
#   scripts/bale-debug.sh ffmpeg-tune [readahead=on|off|default] [readsize=N|default] [prefetch=N|default] [buffer=ms|default] [ahead=N|default] [delay=N|default] [prio=N|default] [maxres=0|1|default] [dpb=N|default] [grain=dav1d|gpu|off|on|default]
#                                               no args = current overrides; omitted keys unchanged; default restores that key's release setting
#                                               next FFmpeg player attempt only; on = 64 MiB / 30 s, readsize 16384–1048576 bytes, prefetch 2–16 blocks, buffer = MediaStreamSource.BufferTime 0–5000 ms (FFmpegInteropX sets 0)
#                                               software video only: ahead 0–64 decoded frames (0=off), delay 0–64 dav1d frames (0=dav1d default)
#                                               prio 0–2 (0=off, 1=THREAD_PRIORITY_ABOVE_NORMAL, 2=THREAD_PRIORITY_HIGHEST); software video only, release default unset
#                                               grain=default: gpu (production default); on/dav1d: dav1d; gpu: GPU; off: export-only (software video; wrapper 2.1.0-bale.10+)
#                                               software AV1 is fixed to zero-copy direct write (upload variant 6) with GPU grain; streams it can't take transcode on the server
#                                               system/passthrough video only: maxres 0–1, dpb 0–16 (0=unset); both default unset, opt-in pending wrapper release/repin
#                                               software-video release defaults: ahead=16, buffer=1000 ms, delay unset; above 1080p (width >1920 or height >1080) ahead=8, buffer=500 ms, delay=6
#                                               key i / where show applied settings and demand block waits (count/max/total ms)
#   scripts/bale-debug.sh asstest [ms out.png]   libass in bale_gpu.dll: version, DirectWrite font lookups/fallback, render timings; with ms, saves the probe script frame as PNG
#   scripts/bale-debug.sh rust                   Rust loader + GPU film grain: hardware/self-tests, 1080p correctness, 1080p/4K GPU and CPU timings (120 s timeout)
#   scripts/bale-debug.sh mem [gc]               managed GC heap vs app memory; gc forces a full collection + finalizers first
#   scripts/bale-debug.sh gc                     GC mode, playback lease, total pauses and last full-blocking/background/ephemeral collections
#   scripts/bale-debug.sh gc latency <batch|interactive|sustained|default>  change latency app-wide now; default restores the saved pre-playback mode; playback acquires sustained automatically
#   scripts/bale-debug.sh frame-delivery [native|managed|off]  next playback attempt's observer; default native, on aliases native; memory only (restart resets); no args = status
#   scripts/bale-debug.sh stream-adapter [pooled|builtin]  next FFmpeg playback attempt's stream adapter; default pooled, memory only (restart resets); no args = status
#   scripts/bale-debug.sh av1-claim [1080|2160]   next playback attempt's AV1 software-decode claim at 30 fps; default 1080, memory only (restart resets); no args = status
#   scripts/bale-debug.sh av1-fps [30|60]       next playback attempt's AV1 software-decode claim frame rate; default 30, memory only (restart resets); no args = status
#   scripts/bale-debug.sh display-match [on|off|status]  match content HDMI resolution/refresh on the next playback; default off, memory only (restart resets); no args = status
#   scripts/bale-debug.sh hdr pretend|off|status  next attempt: pretend HDR10, skip HDMI switching for console SDR tone mapping; default off, memory only
#   scripts/bale-debug.sh idle-test [display-pulse|smtc-playing|smtc-stopped|status]  input-idle experiments: pulse a DisplayRequest or set the media session status (player's, else the view's); no args = status
#   scripts/bale-debug.sh webview-test [open-video|open-blank|close|status]  input-idle comparison: small WebView2 popup looping a muted sample or a blank page; no args = status
#   scripts/bale-debug.sh seek <seconds> [pause]  shared absolute-source seek (native/FFmpeg/HLS, reanchors outside the mounted window); optional pause
#   scripts/bale-debug.sh key j                 player: skip back 10s (same as press lt); one skip per press, ignores repeats
#   scripts/bale-debug.sh key l                 player: skip forward 30s (same as press rt); passive bar/chip hides after 1200ms
#   scripts/bale-debug.sh key i                 player: toggle stats (same as press view); where includes counters even when hidden
#   scripts/bale-debug.sh caps [section]        installed decoders, GPU decode profiles, display modes, platform, audio mix and baseline
#                                               (section: video, gpu, audio, display, platform, device or graph)
#   scripts/bale-debug.sh playtest <url> [seconds]  muted public http(s) stream probe (1–10 s, default 3)
#   timeout 300 scripts/bale-debug.sh playtest suite [seconds]  Chromium samples, one at a time
#                                               (the whole suite needs more than the usual timeout 20)
#   scripts/bale-debug.sh playtest-engine <url|@file|suite> [seconds] [cs|rust|rust-ffmpeg]  debug IMFMediaEngine frame-server probe (1–60 s, default 3; rust = bale_gpu.dll port; rust-ffmpeg = FFmpeg software source)
#   scripts/bale-debug.sh playtest-ffmpeg <url|@file|suite> [seconds] [mode] [threads=N] [10bit=on|off] [rate=R] [ahead=N] [delay=N] [prio=N] [maxres=0|1|default] [dpb=N|default] [hdr=on|off|default] [grain=dav1d|gpu|off|on|default]
#                                               hdr=on|off forces the wrapper's HdrSupport Enabled/Disabled (default omitted = Automatic: HDR colour info only if HDMI is already BT.2020)
#                                               software mode uses the production path (zero-copy, upload variant 6, GPU grain); grain=on/dav1d: dav1d; gpu: GPU; off exports only (wrapper 2.1.0-bale.10+)
#                                               debug FFmpeg probe (1–60 s, default 3; auto|software|system, default auto)
#                                               tuning after seconds/mode, any order: threads 0–16 (0=CPU count), rate 0.5–2.0 (default 1)
#                                               software video only: ahead 0–64 (0=off), delay 0–64 (0=dav1d default)
#                                               prio 0–2 (0=off, 1=THREAD_PRIORITY_ABOVE_NORMAL, 2=THREAD_PRIORITY_HIGHEST); software video only, default unset
#                                               system/passthrough video only: maxres 0–1, dpb 0–16 (0=unset); default = omitted, both unset unless supplied; pending wrapper release/repin
#                                               software mode defaults: ahead=16, GPU grain; delay unset (URI size unknown before open; use delay=6 for known above-1080p media)
#                                               auto/system retain wrapper defaults unless overridden; reports actual settings/output and software frame delivery
#   timeout 900 scripts/bale-debug.sh playtest-ffmpeg suite 15 software  same Chromium samples; allow 30 s create + 15 s open per clip
#   scripts/bale-debug.sh subtest <url> [native|ffmpeg] [seconds] [sidecar-url] [idx-url]  muted subtitle probe: lists
#                                               timed-metadata tracks and the cues each raises (text/image, timing, styling)
#                                               BALE_SUBTEST_SHOW=<track> plays full screen with Windows drawing that track
#   scripts/bale-debug.sh suboverlay [stats|load <url> [font-url...]|clear]  Bale's own subtitle renderer over the open player:
#                                               load a subtitle file (.srt = SubRip, .vtt = WebVTT, else ASS/SSA; fonts are added first),
#                                               clear it, or print its counters (default)
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
  stats)
    if [[ "$#" != 1 ]]; then
      echo 'usage: scripts/bale-debug.sh stats' >&2
      exit 2
    fi
    curl --fail-with-body -sS --max-time 5 "$BASE/stats"
    ;;
  threads)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && ! "$2" =~ ^([1-9]|[12][0-9]|30)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh threads [1-30 seconds, default 10]' >&2
      exit 2
    fi
    curl --fail-with-body -sS --max-time 40 -G "$BASE/threads" ${2:+--data-urlencode "seconds=$2"}
    ;;
  frame-trace)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && "$2" != reset ) ]]; then
      echo 'usage: scripts/bale-debug.sh frame-trace [reset]' >&2
      exit 2
    fi
    if [[ "${2-}" == reset ]]; then
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/frame-trace" --data-urlencode "action=reset"
    else
      curl --fail-with-body -sS --max-time 5 "$BASE/frame-trace"
    fi
    ;;
  stats-rows)
    stats_rows_action="${2-list}"
    valid_stats_rows=false
    case "$stats_rows_action" in
      list|reset) [[ "$#" -le 2 ]] && valid_stats_rows=true ;;
      hide|show|only)
        if [[ "$#" == 3 && "$3" != *[[:cntrl:]]* && ! "$3" =~ (^|,)[[:space:]]*(,|$) ]]; then
          valid_stats_rows=true
        fi
        ;;
    esac
    if [[ "$valid_stats_rows" != true ]]; then
      echo 'usage: scripts/bale-debug.sh stats-rows [list|hide <labels>|show <labels>|only <labels>|reset] (one non-empty comma-separated argument)' >&2
      exit 2
    fi
    if [[ "$stats_rows_action" == list ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/stats-rows"
    elif [[ "$stats_rows_action" == reset ]]; then
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/stats-rows" --data-urlencode "action=reset"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/stats-rows" \
        --data-urlencode "action=$stats_rows_action" --data-urlencode "labels=$3"
    fi
    ;;
  caps) curl -fsS --max-time 18 -G "$BASE/caps" ${2:+--data-urlencode "only=$2"} ;;
  ffmpeg)
    if [[ "$#" != 2 || ! "${2:-}" =~ ^(policy|direct|off)$ ]]; then
      echo 'usage: scripts/bale-debug.sh ffmpeg {policy|direct|off}' >&2
      exit 2
    fi
    curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/ffmpeg" --data-urlencode "mode=$2"
    ;;
  ffmpeg-tune)
    shift
    ffmpeg_tune_args=()
    declare -A ffmpeg_tune_seen=()
    for option in "$@"; do
      name="${option%%=*}"
      value="${option#*=}"
      valid_tuning=false
      case "$option" in
        readahead=*) [[ "$value" =~ ^(on|off|default)$ ]] && valid_tuning=true ;;
        readsize=*)
          if [[ "$value" == default ]] || { [[ "$value" =~ ^[0-9]{1,7}$ ]] && (( 10#$value >= 16384 && 10#$value <= 1048576 )); }; then valid_tuning=true; fi
          ;;
        prefetch=*)
          if [[ "$value" == default ]] || { [[ "$value" =~ ^[0-9]{1,7}$ ]] && (( 10#$value >= 2 && 10#$value <= 16 )); }; then valid_tuning=true; fi
          ;;
        buffer=*)
          if [[ "$value" == default ]] || { [[ "$value" =~ ^[0-9]{1,7}$ ]] && (( 10#$value <= 5000 )); }; then valid_tuning=true; fi
          ;;
        ahead=*|delay=*) [[ "$value" == default || "$value" =~ ^0*([0-9]|[1-5][0-9]|6[0-4])$ ]] && valid_tuning=true ;;
        prio=*) [[ "$value" == default || "$value" =~ ^0*[0-2]$ ]] && valid_tuning=true ;;
        maxres=*) [[ "$value" == default || "$value" =~ ^0*[01]$ ]] && valid_tuning=true ;;
        dpb=*) [[ "$value" == default || "$value" =~ ^0*([0-9]|1[0-6])$ ]] && valid_tuning=true ;;
        grain=*) [[ "$value" =~ ^(dav1d|gpu|off|on|default)$ ]] && valid_tuning=true ;;
      esac
      if [[ "$valid_tuning" != true ]] || [[ -n "${ffmpeg_tune_seen[$name]:-}" ]]; then
        echo 'usage: scripts/bale-debug.sh ffmpeg-tune [readahead=on|off|default] [readsize=16384..1048576|default] [prefetch=2..16|default] [buffer=0..5000|default] [ahead=0..64|default] [delay=0..64|default] [prio=0..2|default] [maxres=0|1|default] [dpb=0..16|default] (system video only, 0=unset) [grain=dav1d|gpu|off|on|default] (software video only); any order, no duplicates; no args = status; default restores release settings' >&2
        echo 'software video defaults: ahead=16, buffer=1000 ms, delay unset; above 1080p (width >1920 or height >1080) ahead=8, buffer=500 ms, delay=6; prio unset (0=off, 1=above normal, 2=highest); system video defaults: maxres/dpb unset' >&2
        echo 'software AV1 is fixed to zero-copy direct write (upload variant 6, wrapper 2.1.0-bale.17+) with GPU grain; grain=default: gpu, on/dav1d: dav1d, off: export-only' >&2
        exit 2
      fi
      ffmpeg_tune_seen[$name]=true
      ffmpeg_tune_args+=(--data-urlencode "$option")
    done
    if (( $# == 0 )); then
      curl --fail-with-body -sS --max-time 5 "$BASE/ffmpeg-tune"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/ffmpeg-tune" "${ffmpeg_tune_args[@]}"
    fi
    ;;
  asstest)
    if [[ "$#" -eq 1 ]]; then
      curl --fail-with-body -sS --max-time 120 "$BASE/asstest"
    elif [[ "$#" -eq 3 && "$2" =~ ^[0-9]+$ ]]; then
      curl --fail-with-body -sS --max-time 120 "$BASE/asstest?png=$2" | base64 -d > "$3"
      echo "wrote $3"
    else
      echo 'usage: scripts/bale-debug.sh asstest [<ms> <out.png>]' >&2
      exit 2
    fi
    ;;
  rust)
    if [[ "$#" -ne 1 ]]; then
      echo 'usage: scripts/bale-debug.sh rust' >&2
      exit 2
    fi
    curl --fail-with-body -sS --max-time 120 "$BASE/rust"
    ;;
  mem)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && "$2" != gc ) ]]; then
      echo 'usage: scripts/bale-debug.sh mem [gc]' >&2
      exit 2
    fi
    if [[ "${2-}" == gc ]]; then
      curl --fail-with-body -sS --max-time 60 "$BASE/mem?gc=1"
    else
      curl --fail-with-body -sS --max-time 5 "$BASE/mem"
    fi
    ;;
  gc)
    value="${3-}"
    if [[ "$#" != 1 && ( "$#" != 3 || "${2-}" != latency || ! "$value" =~ ^(batch|interactive|sustained|default)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh gc [latency <batch|interactive|sustained|default>]' >&2
      exit 2
    fi
    if [[ "$#" == 1 ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/gc"
    else
      curl --fail-with-body -sS --max-time 5 -X POST "$BASE/gc?latency=$value"
    fi
    ;;
  frame-delivery)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && ! "$2" =~ ^(native|managed|off|on)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh frame-delivery [native|managed|off]' >&2
      exit 2
    fi
    if [[ "$#" == 1 ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/frame-delivery"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/frame-delivery" --data-urlencode "mode=$2"
    fi
    ;;
  stream-adapter)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && ! "$2" =~ ^(pooled|builtin)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh stream-adapter [pooled|builtin]' >&2
      exit 2
    fi
    if [[ "$#" == 1 ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/stream-adapter"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/stream-adapter" --data-urlencode "mode=$2"
    fi
    ;;
  av1-claim)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && ! "$2" =~ ^(1080|2160)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh av1-claim [1080|2160]' >&2
      exit 2
    fi
    if [[ "$#" == 1 ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/av1-claim"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/av1-claim" --data-urlencode "height=$2"
    fi
    ;;
  av1-fps)
    if [[ "$#" -gt 2 || ( "$#" -eq 2 && ! "$2" =~ ^(30|60)$ ) ]]; then
      echo 'usage: scripts/bale-debug.sh av1-fps [30|60]' >&2
      exit 2
    fi
    if [[ "$#" == 1 ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/av1-fps"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/av1-fps" --data-urlencode "fps=$2"
    fi
    ;;
  display-match)
    if [[ "$#" -gt 2 || ! "${2-status}" =~ ^(on|off|status)$ ]]; then
      echo 'usage: scripts/bale-debug.sh display-match [on|off|status]' >&2
      exit 2
    fi
    if [[ "${2-status}" == status ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/display-match"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/display-match" --data-urlencode "mode=$2"
    fi
    ;;
  idle-test)
    if [[ "$#" -gt 2 || ! "${2-status}" =~ ^(display-pulse|smtc-playing|smtc-stopped|status)$ ]]; then
      echo 'usage: scripts/bale-debug.sh idle-test [display-pulse|smtc-playing|smtc-stopped|status]' >&2
      exit 2
    fi
    if [[ "${2-status}" == status ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/idle-test"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/idle-test" --data-urlencode "action=$2"
    fi
    ;;
  webview-test)
    if [[ "$#" -gt 2 || ! "${2-status}" =~ ^(open-video|open-blank|close|status)$ ]]; then
      echo 'usage: scripts/bale-debug.sh webview-test [open-video|open-blank|close|status]' >&2
      exit 2
    fi
    if [[ "${2-status}" == status ]]; then
      curl --fail-with-body -sS --max-time 10 "$BASE/webview-test"
    else
      curl --fail-with-body -sS --max-time 30 -X POST -G "$BASE/webview-test" --data-urlencode "action=$2"
    fi
    ;;
  hdr)
    if [[ "$#" != 2 || ! "${2:-}" =~ ^(pretend|off|status)$ ]]; then
      echo 'usage: scripts/bale-debug.sh hdr {pretend|off|status}' >&2
      exit 2
    fi
    if [[ "$2" == status ]]; then
      curl --fail-with-body -sS --max-time 5 "$BASE/hdr"
    else
      curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/hdr" --data-urlencode "mode=$2"
    fi
    ;;
  seek)
    if [[ "$#" -lt 2 || "$#" -gt 3 || ! "${2:-}" =~ ^[0-9]+([.][0-9]+)?$ || ( "$#" == 3 && "$3" != pause ) ]]; then
      echo 'usage: scripts/bale-debug.sh seek <non-negative-seconds> [pause] (may reanchor HLS outside its mounted window)' >&2
      exit 2
    fi
    seek_pause=false
    if [[ "${3:-}" == pause ]]; then seek_pause=true; fi
    curl --fail-with-body -sS --max-time 45 -X POST -G "$BASE/seek" \
      --data-urlencode "seconds=$2" --data-urlencode "pause=$seek_pause"
    ;;
  subtest)
    if [[ "$#" -lt 2 || "$#" -gt 6 || ! "${3:-native}" =~ ^(native|ffmpeg)$ || ! "${4:-31}" =~ ^([1-9]|[1-3][0-9]|40)$
          || ! "${BALE_SUBTEST_SHOW:-0}" =~ ^([0-9]|[12][0-9]|3[01])$ ]]; then
      echo 'usage: [BALE_SUBTEST_SHOW=<track 0–31>] scripts/bale-debug.sh subtest <http(s)-url> [native|ffmpeg] [seconds 1–40, default 31] [sidecar-url] [vobsub-idx-url]' >&2
      exit 2
    fi
    curl --fail-with-body -sS --max-time "$(( ${4:-31} + 60 ))" -G "$BASE/subtest" --data-urlencode "url=$2" \
      --data-urlencode "route=${3:-native}" --data-urlencode "seconds=${4:-31}" \
      ${5:+--data-urlencode "sidecar=$5"} ${6:+--data-urlencode "index=$6"} \
      ${BALE_SUBTEST_SHOW:+--data-urlencode "show=$BALE_SUBTEST_SHOW"}
    ;;
  suboverlay)
    case "${2:-stats}" in
      stats)
        if [[ "$#" -gt 2 ]]; then
          echo 'usage: scripts/bale-debug.sh suboverlay [stats|load <http(s)-url> [font-url...]|clear]' >&2
          exit 2
        fi
        curl --fail-with-body -sS --max-time 5 "$BASE/suboverlay"
        ;;
      load)
        if [[ "$#" -lt 3 || "$#" -gt 19 ]]; then
          echo 'usage: scripts/bale-debug.sh suboverlay load <http(s)-url> [font-url, up to 16...]' >&2
          exit 2
        fi
        suboverlay_args=(--data-urlencode "action=load" --data-urlencode "url=$3")
        for suboverlay_font in "${@:4}"; do
          suboverlay_args+=(--data-urlencode "font=$suboverlay_font")
        done
        # The app allows 15 s for the headers and 15 s for the body of each download.
        curl --fail-with-body -sS --max-time "$(( 40 + 30 * ($# - 3) ))" -X POST -G "$BASE/suboverlay" "${suboverlay_args[@]}"
        ;;
      clear)
        if [[ "$#" -ne 2 ]]; then
          echo 'usage: scripts/bale-debug.sh suboverlay clear' >&2
          exit 2
        fi
        curl --fail-with-body -sS --max-time 5 -X POST -G "$BASE/suboverlay" --data-urlencode "action=clear"
        ;;
      *)
        echo 'usage: scripts/bale-debug.sh suboverlay [stats|load <http(s)-url> [font-url...]|clear]' >&2
        exit 2
        ;;
    esac
    ;;
  playtest|playtest-ffmpeg|playtest-engine)
    playtest_route="$1"
    playtest_seconds="${3:-3}"
    playtest_max_seconds=10
    playtest_max_args=3
    playtest_open_margin=9
    playtest_mode_args=()
    if [[ "$playtest_route" == playtest-engine ]]; then
      playtest_max_seconds=60
      playtest_max_args=4
      # 20 s engine open budget plus teardown (rust-ffmpeg adds up to 30 s FFmpeg create).
      playtest_open_margin=30
      [[ "${4:-cs}" == rust-ffmpeg ]] && playtest_open_margin=60
      playtest_mode_args=(--data-urlencode "mode=${4:-cs}")
    fi
    if [[ "$playtest_route" == playtest-ffmpeg ]]; then
      playtest_max_seconds=60
      playtest_max_args=14
      # 30 s native create + 15 s MediaOpened + 5 s response/cleanup margin.
      playtest_open_margin=50
      playtest_mode_args=(--data-urlencode "mode=${4:-auto}")
    fi
    if [[ "$#" -lt 2 || "$#" -gt "$playtest_max_args" || ! "$playtest_seconds" =~ ^([1-9]|[1-5][0-9]|60)$ ]] \
      || (( playtest_seconds > playtest_max_seconds )) \
      || [[ "$playtest_route" == playtest-ffmpeg && ! "${4:-auto}" =~ ^(auto|software|system)$ ]] \
      || [[ "$playtest_route" == playtest-engine && ! "${4:-cs}" =~ ^(cs|rust|rust-ffmpeg)$ ]]; then
      echo 'usage: scripts/bale-debug.sh playtest <http(s)-url|suite> [seconds] (1–10, default 3; suite needs timeout 300)' >&2
      echo '       scripts/bale-debug.sh playtest-engine <http(s)-url|@file|suite> [seconds] [cs|rust|rust-ffmpeg] (1–60 s, default 3; default cs)' >&2
      echo '       scripts/bale-debug.sh playtest-ffmpeg <http(s)-url|@file|suite> [seconds] [auto|software|system] [threads=N] [10bit=on|off] [rate=R] [ahead=N] [delay=N] [prio=N] [maxres=0|1|default] [dpb=N|default] [hdr=on|off|default] [grain=dav1d|gpu|off|on|default] (1–60 s, default 3; tuning requires seconds/mode)' >&2
      exit 2
    fi
    if [[ "$playtest_route" == playtest-ffmpeg ]]; then
      declare -A playtest_tuning_seen=()
      for option in "${@:5}"; do
        name="${option%%=*}"
        value="${option#*=}"
        valid_tuning=false
        case "$option" in
          ahead=*|delay=*) [[ "$value" =~ ^0*([0-9]|[1-5][0-9]|6[0-4])$ ]] && valid_tuning=true ;;
          prio=*) [[ "$value" =~ ^0*[0-2]$ ]] && valid_tuning=true ;;
          maxres=*) [[ "$value" == default || "$value" =~ ^0*[01]$ ]] && valid_tuning=true ;;
          dpb=*) [[ "$value" == default || "$value" =~ ^0*([0-9]|1[0-6])$ ]] && valid_tuning=true ;;
          hdr=*) [[ "$value" =~ ^(on|off|default)$ ]] && valid_tuning=true ;;
          grain=*) [[ "$value" =~ ^(dav1d|gpu|off|on|default)$ ]] && valid_tuning=true ;;
          threads=*) [[ "$value" =~ ^0*([0-9]|1[0-6])$ ]] && valid_tuning=true ;;
          10bit=*) [[ "$value" =~ ^(on|off)$ ]] && valid_tuning=true ;;
          rate=*) [[ "$value" =~ ^(0*[.][5-9][0-9]*|0*1([.][0-9]*)?|0*2([.]0*)?)$ ]] && valid_tuning=true ;;
        esac
        if [[ "$valid_tuning" != true || -n "${playtest_tuning_seen[$name]:-}" ]]; then
          echo 'tuning after seconds/mode: [threads=N] [10bit=on|off] [rate=R] [ahead=N] [delay=N] [prio=N] [maxres=0|1|default] [dpb=N|default] [hdr=on|off|default] [grain=dav1d|gpu|off|on|default], any order, no duplicates; N=0–16 (0=CPU count), R=0.5–2.0, ahead/delay=0–64, prio=0–2 (0=off, 1=above normal, 2=highest); system video only: maxres=0–1, dpb=0–16 (0=unset), default = omitted; hdr=on|off forces wrapper HdrSupport Enabled/Disabled (default Automatic); software mode uses the production zero-copy path (upload variant 6, GPU grain); grain=on/dav1d keeps dav1d, gpu=GPU, off=export-only (wrapper 2.1.0-bale.10+)' >&2
          exit 2
        fi
        playtest_tuning_seen[$name]=true
        playtest_mode_args+=(--data-urlencode "$option")
      done
    fi
    playtest_url() {
      local url_data="url=$1"
      if [[ "$1" == @* ]]; then
        if [[ ! -r "${1#@}" ]]; then
          echo 'URL file is not readable' >&2
          return 2
        fi
        # curl reads the file directly: the URL never reaches output or curl's argument list.
        url_data="url@${1#@}"
      fi
      curl --fail-with-body -sS --max-time "$((playtest_seconds + playtest_open_margin))" -G "$BASE/$playtest_route" \
        --data-urlencode "$url_data" --data-urlencode "seconds=$playtest_seconds" "${playtest_mode_args[@]}"
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
        printf '\n=== %s ===\n' "$sample"
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
