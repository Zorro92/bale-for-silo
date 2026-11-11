#!/usr/bin/env bash
# Lists upstream Silo commits not yet reviewed in docs/upstream-log.md.
#
#   SILO_REFERENCE_DIR=<dir with the clones> scripts/upstream-check.sh status
#       fast-forward every clone and count unreviewed commits
#   scripts/upstream-check.sh log <repo> [--stat]
#       unreviewed commits, oldest first (merges skipped)
#   scripts/upstream-check.sh advance <repo> [commit]
#       mark the repo reviewed up to a commit (default: origin's head)
#
# The clones are plain `git clone`s of github.com/Silo-Server/<repo>, named after the repo.
# The repos and their last reviewed commits are the "Last reviewed" table in the log.
set -euo pipefail

ROOT="${SILO_REFERENCE_DIR:?Set SILO_REFERENCE_DIR to the directory holding the Silo clones}"
LOG="$(cd "$(dirname "$0")/.." && pwd)/docs/upstream-log.md"

repos() { sed -n 's/^| \(silo-[a-z0-9-]*\) | `\([0-9a-f]*\)` | .*/\1 \2/p' "$LOG"; }
reviewed() { repos | awk -v r="$1" '$1 == r { print $2 }'; }
head_of() { git -C "$ROOT/$1" rev-parse "$(git -C "$ROOT/$1" symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo origin/main)"; }
need_repo() {
  [[ -n "$(reviewed "$1")" ]] || { echo "unknown repo: $1 (not in $LOG)" >&2; exit 2; }
  [[ -d "$ROOT/$1/.git" ]] || { echo "no clone at \$SILO_REFERENCE_DIR/$1" >&2; exit 2; }
}

case "${1:-}" in
  status)
    while read -r repo base; do
      if [[ ! -d "$ROOT/$repo/.git" ]]; then echo "$repo: no clone"; continue; fi
      git -C "$ROOT/$repo" fetch -q origin
      git -C "$ROOT/$repo" merge -q --ff-only "$(head_of "$repo")" >/dev/null 2>&1 || true
      tip="$(head_of "$repo")"
      count="$(git -C "$ROOT/$repo" rev-list --count --no-merges "$base..$tip")"
      echo "$repo: $count unreviewed (reviewed ${base:0:12}, head ${tip:0:12})"
    done < <(repos)
    ;;
  log)
    need_repo "${2:?repo}"
    stat=()
    [[ "${3:-}" == "--stat" ]] && stat=(--stat=120)
    git -C "$ROOT/$2" log --reverse --no-merges --format='%h %cs %s' "${stat[@]}" "$(reviewed "$2")..$(head_of "$2")"
    ;;
  advance)
    need_repo "${2:?repo}"
    commit="$(git -C "$ROOT/$2" rev-parse "${3:-$(head_of "$2")}")"
    sed -i "s/^| $2 | \`[0-9a-f]*\` | [0-9-]* |/| $2 | \`${commit:0:12}\` | $(date +%F) |/" "$LOG"
    echo "$2 reviewed up to ${commit:0:12}"
    ;;
  *)
    sed -n '/^#/{s/^# \{0,1\}//;p;};/^set /q' "$0" | tail -n +2
    exit 2
    ;;
esac
