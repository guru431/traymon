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
#      published without a word.
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

new_repo() {
  repo=$(mktemp -d)
  git -C "$repo" init -q
  git -C "$repo" config user.email traymon@example.invalid
  git -C "$repo" config user.name TrayMon
  git -C "$repo" config core.hooksPath "$hooks"
  git -C "$repo" config commit.gpgsign false
  echo "$repo"
}

# 1) A token after a '#' in a `-m` message. The whole class the hook missed.
repo=$(new_repo)
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

rm -rf "$repo"
echo ""
echo "passed $passed, failed $failed"
[ "$failed" -eq 0 ]
