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
  # regex in the file blocks the commit rather than silently passing it.
  # The MAIN worktree's root, not this worktree's. `--show-toplevel` in a
  # `git worktree` (superpowers using-git-worktrees, Agent isolation:
  # "worktree") points at the linked checkout, where this untracked, gitignored
  # file does not exist — so the entire internal-hosts / IP / personal-data
  # class silently went unchecked, and CI cannot cover it either (gitleaks has
  # no access to the denylist). Fall back to a shared user-level copy, and if
  # there is none, SAY so instead of skipping in silence.
  scan_common=$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)
  if [ -n "$scan_common" ]; then
    scan_sp="$(dirname "$scan_common")/.sanitize-patterns"
  else
    scan_sp="$(git rev-parse --show-toplevel)/.sanitize-patterns"
  fi
  if [ ! -f "$scan_sp" ] && [ -f "${XDG_CONFIG_HOME:-$HOME/.config}/traymon/sanitize-patterns" ]; then
    scan_sp="${XDG_CONFIG_HOME:-$HOME/.config}/traymon/sanitize-patterns"
  fi
  if [ ! -f "$scan_sp" ]; then
    echo "WARN: .sanitize-patterns not found (looked in $scan_sp) — the personal-data check is SKIPPED."
  fi
  if [ -f "$scan_sp" ]; then
    scan_pat=$(mktemp 2>/dev/null || echo "$scan_sp.tmp")
    grep -vE '^[[:space:]]*$' "$scan_sp" 2>/dev/null | tr -d '' > "$scan_pat" || true
    if [ -s "$scan_pat" ]; then
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
