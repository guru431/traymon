# shellcheck shell=sh
# Shared secret/personal-data scanner for the PUBLIC repository's git hooks.
#
# Sourced by `.githooks/pre-commit` (staged diff) and `.githooks/commit-msg`
# (the commit message). The message needed its own gate: the hook only ever
# looked at `git diff --cached`, so a secret or an internal hostname typed into
# `git commit -m` passed every local check — and CI cannot cover that class
# either, because the concrete-values denylist lives in `.sanitize-patterns`,
# which is untracked and gitignored by design. On a public repo the value is
# disclosed the moment it is pushed, and `--amend` does not retract it from
# mirrors or forks.
#
# Contract: `scan_text <label> <text>` prints findings and returns 1 when the
# text must not be committed, 0 when it is clean.

# High-confidence secret/token formats (generic, very low false-positive).
# Kept in ONE place so the two hooks can never drift apart on what a secret is.
scan_generic_pattern() {
  printf '%s' '-----BEGIN [A-Z ]*PRIVATE KEY-----|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|gho_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-ant-[A-Za-z0-9_-]{20,}|sk-[A-Za-z0-9_-]{16,}|gsk_[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_-]{35}|ccr-[A-Za-z0-9]{8,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+|[0-9]{8,10}:[A-Za-z0-9_-]{35}|user_[A-Za-z0-9]{20,}'
}

# Where the personal denylist lives, in ONE place: pre-push used to resolve it
# on its own with `--show-toplevel`, which points at a linked worktree — where
# this untracked, gitignored file does not exist — so a blob carrying only a
# personal marker could be pushed from a worktree with the check silently
# skipped, while `scan_text` looked in the right place. Two resolvers, two
# answers, and the difference decided whether a secret was published.
# Prints the path (which may not exist) on stdout.
# Returns non-zero with a diagnostic when no repository root can be resolved.
# Made absolute with cd, not with --path-format=absolute: that is Git 2.31+,
# and an older rev-parse echoes an option it does not know back as output
# instead of failing — a non-empty answer pointing nowhere, the worktree bug
# again. --git-common-dir alone is Git 2.5+; on anything older its echo is not
# a directory, and the top level is the best there is.
scan_denylist_path() {
  scan_common=$(git rev-parse --git-common-dir 2>/dev/null || true)
  if [ -n "$scan_common" ] && [ -d "$scan_common" ]; then
    scan_sp="$(dirname "$(cd "$scan_common" && pwd)")/.sanitize-patterns"
  else
    scan_root=$(git rev-parse --show-toplevel 2>/dev/null) || scan_root=
    if [ -z "$scan_root" ]; then
      echo "BLOCKED: cannot determine the repository root — the personal-data check cannot run." >&2
      return 1
    fi
    scan_sp="$scan_root/.sanitize-patterns"
  fi
  if [ ! -f "$scan_sp" ] && [ -f "${XDG_CONFIG_HOME:-$HOME/.config}/traymon/sanitize-patterns" ]; then
    scan_sp="${XDG_CONFIG_HOME:-$HOME/.config}/traymon/sanitize-patterns"
  fi
  printf '%s' "$scan_sp"
}

# The denylist at <path> as patterns for `grep -Ef`, one per line, on stdout:
# blank lines dropped (an empty pattern matches everything) and CRs too (a
# CRLF-saved file would leave a trailing \r on every line and match nothing).
# Returns non-zero when the file cannot be read, and both callers refuse then.
# The preparation used to hide that behind `2>/dev/null ... || true`: an
# unreadable file came back as an empty list, read as "no patterns", and the
# personal-data check was skipped without a word — the silent pass that failing
# closed on a broken regex was there to rule out.
scan_denylist_patterns() {
  scan_draw=$(tr -d '\r' < "$1") || return 2
  printf '%s\n' "$scan_draw" | grep -vE '^[[:space:]]*$'
  [ "$?" -le 1 ] || return 2
}

# scan_text <label> <text> -> 0 clean, 1 blocked
scan_text() {
  scan_label=$1
  scan_body=$2
  scan_fail=0

  scan_hits=$(printf '%s\n' "$scan_body" | grep -nE -e "$(scan_generic_pattern)" || true)
  if [ -n "$scan_hits" ]; then
    echo "BLOCKED: possible secret/token format in $scan_label:"
    printf '%s\n' "$scan_hits" | sed 's/^/  /'
    scan_fail=1
  fi

  # Personal denylist — the untracked .sanitize-patterns. Matched as EXTENDED
  # REGEX (-E), because that is what this repo's denylist is written in:
  # `192\.168\.1\.[0-9]+`, `0nk\.ru`. Reading it with -F would both miss every
  # such line and, on Git Bash, abort grep outright (rc=134) instead of
  # reporting anything. A scanner error (grep rc>1) fails closed, so a broken
  # regex in the file blocks the commit rather than silently passing it, and so
  # does a file that exists but cannot be read — see scan_denylist_patterns.
  # The MAIN worktree's root, not this worktree's — see scan_denylist_path,
  # which both hooks now share. Fall back to a shared user-level copy, and if
  # there is none, SAY so instead of skipping in silence.
  scan_sp=$(scan_denylist_path) || return 1
  if [ ! -f "$scan_sp" ]; then
    echo "WARN: .sanitize-patterns not found (looked in $scan_sp) — the personal-data check is SKIPPED."
  fi
  if [ -f "$scan_sp" ]; then
    # The fallback is per process: under one fixed name two hooks running at once — two worktrees
    # of one repository — read each other's half-written pattern list.
    scan_pat=$(mktemp 2>/dev/null || echo "$scan_sp.$$.tmp")
    scan_rc=0
    scan_denylist_patterns "$scan_sp" > "$scan_pat" || scan_rc=$?
    if [ "$scan_rc" -ne 0 ]; then
      echo "BLOCKED: $scan_sp exists but could not be read — refusing."
      scan_fail=1
    elif [ ! -s "$scan_pat" ]; then
      echo "WARN: $scan_sp holds no patterns — the personal-data check is SKIPPED."
    else
      scan_rc=0
      scan_hits=$(printf '%s\n' "$scan_body" | grep -inEf "$scan_pat") || scan_rc=$?
      if [ "$scan_rc" -gt 1 ]; then
        echo "BLOCKED: .sanitize-patterns scan of $scan_label failed (grep rc=$scan_rc) — refusing."
        scan_fail=1
      elif [ -n "$scan_hits" ]; then
        echo "BLOCKED: personal data (matched .sanitize-patterns) in $scan_label:"
        printf '%s\n' "$scan_hits" | sed 's/^/  /'
        scan_fail=1
      fi
    fi
    rm -f "$scan_pat"
  fi

  return "$scan_fail"
}
