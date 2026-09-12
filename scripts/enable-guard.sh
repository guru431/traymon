#!/usr/bin/env bash
# enable-guard.sh — activate the secret-guard hooks for THIS clone of traymon.
#
# Git ignores a custom hook path until you opt in, so .githooks/ sits inert in a
# fresh clone and all three guards (commit-msg, pre-commit, pre-push) protect
# nothing. This repo is mirrored to a PUBLIC github remote, so run it once per
# clone, before the first commit.
set -eu
cd "$(dirname "$0")/.."   # repo root

git config core.hooksPath .githooks
# POSIX git SILENTLY skips a non-executable hook — no warning, zero protection.
chmod +x .githooks/commit-msg .githooks/pre-commit .githooks/pre-push 2>/dev/null || true
echo "[ok] core.hooksPath = .githooks — commit-msg + pre-commit + pre-push secret-guard active"

# Seed a LOCAL, untracked .sanitize-patterns reference listing the CLASSES of
# personal strings to put in the live denylist. Only seed it if git actually
# ignores it, otherwise the seed itself becomes something to leak.
seed=".sanitize-patterns.md"
if ! git check-ignore -q "$seed" 2>/dev/null; then
    echo "[warn] $seed is NOT covered by .gitignore — not seeding it."
    echo "       Add a '.sanitize-patterns*' line to .gitignore first, then re-run."
elif [ ! -e ".sanitize-patterns" ] && [ ! -e "$seed" ]; then
    cat > "$seed" <<'SEED'
# .sanitize-patterns — LOCAL denylist reference (never commit this file)
#
# Create a sibling `.sanitize-patterns` (no extension) with ONE literal string
# per line — the hooks match it with `grep -F`, so nothing is escaped and an
# unbalanced bracket cannot silently disable the check. Both files are
# gitignored and refused by the pre-commit filename rule.
#
# Classes worth adding (put YOUR real values in .sanitize-patterns, not here):
#   - your Windows / Linux usernames
#   - your machine + LAN hostnames
#   - domains of your personally-owned services
#   - the first 6-8 chars of every real API key / bot token you use
#   - specific private LAN IPs
#   - disk serial numbers that appear in TrayMon.json / TrayMon.csv
SEED
    echo "[ok] seeded $seed — copy the classes you need into an untracked .sanitize-patterns"
else
    echo "[skip] .sanitize-patterns / $seed already present — left untouched"
fi

echo ""
echo "Done. Commits and pushes in this clone now run the secret-guard."
echo "Bypass a confirmed false positive with:  git commit --no-verify  /  git push --no-verify"
