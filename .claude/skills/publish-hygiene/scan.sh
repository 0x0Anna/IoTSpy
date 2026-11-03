#!/usr/bin/env bash
# Scan what is about to be published (added lines + commit messages, optionally a PR body)
# for details about the developer's own environment that don't belong in a public repo.
#
# Usage: scan.sh [base-ref] [extra-file...]
#   base-ref     defaults to origin/main; scans base-ref...HEAD plus staged and unstaged changes
#   extra-file   e.g. a PR description draft to scan as well
#
# Patterns come from two places:
#   1. Generic categories below (private IPs, MACs, interface names, home paths, keys, ...)
#   2. .claude/sensitive-patterns.local.txt — the developer's own denylist, one extended
#      regex per line (# comments allowed). Git-ignored: it must never be committed, since
#      listing the private details in a public repo would leak them.
# Exit status: 0 = no hits, 1 = hits found (review each one; not every hit is a leak).
set -uo pipefail
root=$(git rev-parse --show-toplevel)
base=${1:-origin/main}; shift || true
head=${HEAD_REF:-HEAD}   # e.g. HEAD_REF=some-branch to scan a branch that isn't checked out
local_patterns="$root/.claude/sensitive-patterns.local.txt"

generic=(
  '\b(10\.[0-9]{1,3}|172\.(1[6-9]|2[0-9]|3[01])|192\.168)\.[0-9]{1,3}\.[0-9]{1,3}\b'   # private IPv4 (use 192.0.2.x / 198.51.100.x / 203.0.113.x)
  '\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b'                                        # MAC address
  '\b(ens[0-9]+|enp[0-9]+s[0-9]+[a-z0-9]*|eno[0-9]+|wlp[0-9]+s[0-9]+|vmbr[0-9]+|br-[0-9a-f]{12})\b' # host-specific NIC names
  '/home/[A-Za-z0-9_.-]+/|/Users/[A-Za-z0-9_.-]+/'                                     # personal paths
  '\b[A-Za-z0-9-]+\.(lan|home|internal|localdomain)\b'                                 # LAN hostnames
  'BEGIN [A-Z ]*PRIVATE KEY|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{20,}\.' # keys/tokens/JWTs
  '(password|passwd|secret|api[_-]?key)["'"'"' ]*[:=] *["'"'"'][^"'"'"']{6,}'            # inline credentials
  '-9[0-9]{17,18}\b'                                                                   # real-looking signed 64-bit ids
  '\b[0-9a-f]{32}\b'                                                                   # 32-hex (JA3/MD5) — often a real device fingerprint
)

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
{ git -C "$root" diff "$base...$head"
  if [ "$head" = HEAD ]; then git -C "$root" diff --cached; git -C "$root" diff; fi; } \
  | grep -E '^\+[^+]' | sed 's/^+//' > "$tmp/added"
git -C "$root" log --format='%B' "$base..$head" > "$tmp/messages"
for f in "$@"; do cat "$f"; done > "$tmp/extra"

hits=0
scan() { # label pattern [grep flags] — generic patterns are case-sensitive, private ones not
  for src in added messages extra; do
    if out=$(grep -nIE ${3:-} -- "$2" "$tmp/$src" | head -5) && [ -n "$out" ]; then
      echo "[$1] in $src  (/$2/)"; echo "$out" | sed 's/^/    /'; hits=1
    fi
  done
}
for p in "${generic[@]}"; do scan generic "$p"; done
if [ -f "$local_patterns" ]; then
  while IFS= read -r p; do [[ -z "$p" || "$p" == \#* ]] && continue; scan private "$p" -i; done < "$local_patterns"
else
  echo "note: $local_patterns not found — only generic patterns were checked" >&2
fi
[ "$hits" -eq 0 ] && echo "publish-hygiene: no hits ($base...$head)"
exit "$hits"
