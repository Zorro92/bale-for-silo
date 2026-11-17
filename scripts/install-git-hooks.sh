#!/usr/bin/env bash
# Points this clone's git hooks at scripts/git-hooks (pre-push: Rust core build + .NET tests).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
git config core.hooksPath scripts/git-hooks
echo "git hooks: using scripts/git-hooks"
