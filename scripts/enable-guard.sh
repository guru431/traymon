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

# And the chmod is checked, not assumed. It was swallowed by `|| true`, so a hook that stayed
# non-executable — a noexec mount, a filesystem without the bit, a file that was not there at
# all — ended with the same "[ok] ... active" line over a guard git would never run.
rc=0
not_exec=""
for hook in commit-msg pre-commit pre-push; do
    [ -x ".githooks/$hook" ] || not_exec="$not_exec $hook"
done
if [ -n "$not_exec" ]; then
    echo "[warn] not executable:$not_exec — git SKIPS such hooks: the secret-guard is NOT active."
    echo "       Fix the file mode (or remount without noexec) and run this again."
    rc=1
else
    echo "[ok] core.hooksPath = .githooks — commit-msg + pre-commit + pre-push secret-guard active"
fi

# Seed a LOCAL, untracked .sanitize-patterns reference listing the CLASSES of
# personal strings to put in the live denylist. Only seed it if git actually
# ignores it, otherwise the seed itself becomes something to leak.
seed=".sanitize-patterns.md"
live=".sanitize-patterns"
# BOTH names, not just the seed: the file with the real hostnames, tokens and serials is the
# live one, and a .gitignore naming only the seed used to let it be committed — after this
# script had already printed "[ok] ... secret-guard active". The hooks refuse the name, but
# only once the denylist itself is in place.
if ! git check-ignore -q "$live" 2>/dev/null; then
    echo "[warn] $live is NOT covered by .gitignore — the real denylist would be committable."
    echo "       Add a '.sanitize-patterns*' line to .gitignore first, then re-run."
    rc=1
elif ! git check-ignore -q "$seed" 2>/dev/null; then
    echo "[warn] $seed is NOT covered by .gitignore — not seeding it."
    echo "       Add a '.sanitize-patterns*' line to .gitignore first, then re-run."
    rc=1
elif [ ! -e "$live" ] && [ ! -e "$seed" ]; then
    cat > "$seed" <<'SEED'
# .sanitize-patterns — LOCAL denylist reference (never commit this file)
#
# Create a sibling `.sanitize-patterns` (no extension) with ONE pattern per
# line. The hooks match it with `grep -inEf`, so a line is an EXTENDED REGEX:
# escape the dots (`192\.168\.1\.[0-9]+`), and know that a broken regex blocks
# the commit rather than passing it silently. Both files are gitignored and
# refused by the pre-commit filename rule.
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
if [ "$rc" -eq 0 ]; then
    echo "Done. Commits and pushes in this clone now run the secret-guard."
    echo "Bypass a confirmed false positive with:  git commit --no-verify  /  git push --no-verify"
else
    echo "NOT done — see the warnings above. The guard is not protecting this clone yet."
fi
exit "$rc"
