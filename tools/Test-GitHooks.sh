#!/bin/sh
# Regressions for the secret-guard hooks, in throwaway repositories.
#
# Every case here is a way the guard reported "clean" for something it had not
# actually looked at:
#
#   1. `commit-msg` branched on `$2`, which git only passes to
#      `prepare-commit-msg`. The branch that strips `#` lines therefore always
#      ran, while `git commit -m` keeps them — so a secret typed after a `#`
#      passed every local check. Only a real `git commit -m` shows this, which is
#      why this is a shell script and not an xunit test.
#   2. `pre-push` hid the exit codes of rev-list, cat-file and git log behind
#      `|| true`, so "git cannot enumerate these objects" produced an empty list
#      and a clean verdict.
#   3. `pre-push` excluded all of `.githooks/*` from the detailed pass, while
#      `pre-commit` excludes only `_scan.sh` — a token pasted into a hook was
#      published without a word. Case 8 is the one that catches it coming back.
#
# Run (Git Bash on Windows, or any POSIX sh):
#     sh tools/Test-GitHooks.sh
set -eu

hooks=$(cd "$(dirname "$0")/../.githooks" && pwd)
# A fake that matches the generic AWS key shape in _scan.sh. Assembled at run
# time so this file does not itself contain something that looks like a key.
token="AKIA$(printf 'Q7ZK3M2XT9WPLD8F')"
passed=0
failed=0

check() {
  what=$1
  want=$2
  got=$3
  if [ "$want" = "$got" ]; then
    passed=$((passed + 1))
    echo "  ok   $what"
  else
    failed=$((failed + 1))
    echo "  FAIL $what (expected rc=$want, got rc=$got)"
  fi
}

# Called directly, not as $(new_repo): the trap has to be set in this shell, not in a subshell.
new_repo() {
  repo=$(mktemp -d)
  # Removed on any exit, not only at the end: under `set -eu` an unguarded failure part-way
  # through — a `git rev-parse` in case 6 — ended the script and left the directory behind.
  trap 'rm -rf "$repo"' EXIT
  git -C "$repo" init -q
  git -C "$repo" config user.email traymon@example.invalid
  git -C "$repo" config user.name TrayMon
  git -C "$repo" config core.hooksPath "$hooks"
  git -C "$repo" config commit.gpgsign false
}

# 1) A token after a '#' in a `-m` message. The whole class the hook missed.
new_repo
echo one > "$repo/a.txt"
git -C "$repo" add a.txt
rc=0
( cd "$repo" && git commit -q -m "fix thing
# note: key $token" ) >/dev/null 2>&1 || rc=$?
check "commit -m: token on a # line is refused" 1 "$rc"

# 2) An ordinary message still goes through.
rc=0
( cd "$repo" && git commit -q -m "fix thing" ) >/dev/null 2>&1 || rc=$?
check "commit -m: clean message is accepted" 0 "$rc"

# 3) A token in the staged content is refused by pre-commit.
echo "key = $token" > "$repo/b.txt"
git -C "$repo" add b.txt
rc=0
( cd "$repo" && git commit -q -m "add b" ) >/dev/null 2>&1 || rc=$?
check "pre-commit: token in staged content is refused" 1 "$rc"
git -C "$repo" reset -q
rm -f "$repo/b.txt"

# 4) A token in a hook file other than _scan.sh is refused too.
mkdir -p "$repo/.githooks"
echo "example: $token" > "$repo/.githooks/notes.txt"
git -C "$repo" add .githooks/notes.txt
rc=0
( cd "$repo" && git commit -q -m "add hook notes" ) >/dev/null 2>&1 || rc=$?
check "pre-commit: token in .githooks/<other file> is refused" 1 "$rc"
git -C "$repo" reset -q
rm -rf "$repo/.githooks"

# 5) pre-push with an object id that does not exist must fail CLOSED.
missing=ffffffffffffffffffffffffffffffffffffffff
rc=0
printf 'refs/heads/main %s refs/heads/main %s\n' "$missing" "$missing" \
  | ( cd "$repo" && sh "$hooks/pre-push" origin https://example.invalid/x.git ) >/dev/null 2>&1 || rc=$?
check "pre-push: unknown object id refuses the push" 1 "$rc"

# 6) pre-push on the real commit, with nothing excluded, accepts a clean tree.
head=$(git -C "$repo" rev-parse HEAD)
zero=0000000000000000000000000000000000000000
rc=0
printf 'refs/heads/main %s refs/heads/main %s\n' "$head" "$zero" \
  | ( cd "$repo" && sh "$hooks/pre-push" origin https://example.invalid/x.git ) >/dev/null 2>&1 || rc=$?
check "pre-push: clean objects are accepted" 0 "$rc"

# 7) A token in an annotated tag message is refused — nothing looked at these.
git -C "$repo" tag -a v0.0.1-test -m "release $token" >/dev/null 2>&1
tag=$(git -C "$repo" rev-parse v0.0.1-test)
rc=0
printf 'refs/tags/v0.0.1-test %s refs/tags/v0.0.1-test %s\n' "$tag" "$zero" \
  | ( cd "$repo" && sh "$hooks/pre-push" origin https://example.invalid/x.git ) >/dev/null 2>&1 || rc=$?
check "pre-push: token in an annotated tag message is refused" 1 "$rc"

# 8) Regression 3 itself: a token in a blob under .githooks/ other than _scan.sh, refused by
#    pre-push. Case 4 only shows pre-commit refusing it, so with pre-push back on `.githooks/*)
#    continue` everything here stayed green. --no-verify is how such a commit gets past the
#    local hooks in the first place. Dropped again afterwards, so nothing below sees it.
mkdir -p "$repo/.githooks"
echo "example: $token" > "$repo/.githooks/notes.txt"
git -C "$repo" add .githooks/notes.txt
git -C "$repo" commit -q --no-verify -m "add hook notes" >/dev/null 2>&1
head=$(git -C "$repo" rev-parse HEAD)
rc=0
printf 'refs/heads/main %s refs/heads/main %s\n' "$head" "$zero" \
  | ( cd "$repo" && sh "$hooks/pre-push" origin https://example.invalid/x.git ) >/dev/null 2>&1 || rc=$?
check "pre-push: token in .githooks/<other file> is refused" 1 "$rc"
git -C "$repo" reset -q --hard HEAD~1
rm -rf "$repo/.githooks"

# 9) Every LINE of .sanitize-patterns is its own pattern. `grep -Ef` reads the file that way,
#    and the hook pipes it through `tr` first — one character away from deleting the newlines
#    instead of the CRs and leaving a single pattern, the concatenation of all of them, that
#    matches nothing. The hook would still report a completed check, which is how this was read
#    as a live hole in review. CRLF on purpose: that is what the `tr` is there for.
printf 'zaphod\\.example\\.invalid\r\nvogon-[0-9]{4}-serial\r\n' > "$repo/.sanitize-patterns"
for marker in zaphod.example.invalid vogon-4242-serial; do
  echo "host = $marker" > "$repo/c.txt"
  git -C "$repo" add c.txt
  rc=0
  ( cd "$repo" && git commit -q -m "add c" ) >/dev/null 2>&1 || rc=$?
  check "pre-commit: denylist line '$marker' matches on its own" 1 "$rc"
  git -C "$repo" reset -q
done

# And a value that matches neither line still goes through, so the case above is the denylist
# working rather than the hook refusing everything.
echo "host = example.invalid" > "$repo/c.txt"
git -C "$repo" add c.txt
rc=0
( cd "$repo" && git commit -q -m "add c" ) >/dev/null 2>&1 || rc=$?
check "pre-commit: content matching no denylist line is accepted" 0 "$rc"
git -C "$repo" reset -q

# 10) A denylist that exists but cannot be read refuses the commit. Preparing the list hid the read
#    error behind `2>/dev/null ... || true`: the unreadable file became an empty list, the personal
#    check was skipped without a word, and the very value it lists went in. A deny ACE on Windows
#    (chmod does not take read away from the owner on NTFS), mode 000 elsewhere; an account that
#    reads the file anyway — root — cannot show this, and is told so rather than counted.
#    MSYS_NO_PATHCONV: Git Bash otherwise rewrites `/deny` into a path under its install folder.
printf 'zaphod\\.example\\.invalid\n' > "$repo/.sanitize-patterns"
if command -v icacls >/dev/null 2>&1; then
  MSYS_NO_PATHCONV=1 icacls "$(cygpath -w "$repo/.sanitize-patterns")" /deny "$USERDOMAIN\\$USERNAME:(RD)" >/dev/null 2>&1 || true
else
  chmod 000 "$repo/.sanitize-patterns"
fi
if cat "$repo/.sanitize-patterns" >/dev/null 2>&1; then
  echo "  skip pre-commit: unreadable denylist (this account reads it anyway)"
else
  echo "host = zaphod.example.invalid" > "$repo/c.txt"
  git -C "$repo" add c.txt
  rc=0
  ( cd "$repo" && git commit -q -m "add c" ) >/dev/null 2>&1 || rc=$?
  check "pre-commit: a denylist that cannot be read refuses the commit" 1 "$rc"
  git -C "$repo" reset -q
fi
rm -f "$repo/.sanitize-patterns"

echo ""
echo "passed $passed, failed $failed"
[ "$failed" -eq 0 ]
