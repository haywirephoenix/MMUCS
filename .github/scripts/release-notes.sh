#!/usr/bin/env bash
# Release notes for <version>, written to stdout. Used by the Release workflow; run it locally to preview:
#   .github/scripts/release-notes.sh 2.4.0 > notes.md
#
#   1. docs/release-notes/<version>.md, if it exists: the hand-written summary at the top.
#   2. Every commit since the previous release tag, grouped by its "category: " prefix, largest group
#      first, each group folded in a <details>. Subjects are cut at their first ", so " / " (" / "; "
#      (the reason that follows is on the commit), and at about 120 characters.
#   3. The developer groups (tests, mprobe) in one folded block at the end.
set -euo pipefail

v=$1
dev_groups='^(tests|mprobe)$'

# The previous release: the highest version tag below this one (tags are "1.2.3" or "v1.2.3").
# Not `git describe`: it only finds tags on HEAD's history, so a tag left off the branch by a
# history rewrite was skipped and the notes went back a release or more.
prev=$( { git tag --list | grep -E '^v?[0-9]+\.[0-9]+\.[0-9]+$' | awk '{t=$0; sub(/^v/,"",t); print t" "$0}'
          echo "$v __current__"; } | sort -V -k1,1 | awk '$2=="__current__"{print p; exit} {p=$2}')
args=(HEAD)
if [ -z "$prev" ]; then
  echo "No previous release tag: notes list every commit" >&2
elif git merge-base --is-ancestor "$prev" HEAD; then
  args=("$prev..HEAD")
else
  # Its commit was rewritten since: list what was committed after it (rewrites keep the dates).
  since=$(date -u -d "$(git log -1 --format=%cI "$prev") + 1 second" +%Y-%m-%dT%H:%M:%SZ)
  echo "::warning::Tag $prev isn't on this branch (its history was rewritten after the release); notes list commits made after $since" >&2
  args=(--since="$since" HEAD)
fi
echo "Previous release: ${prev:-none}; notes for ${args[*]}" >&2

summary="docs/release-notes/$v.md"
if [ -f "$summary" ]; then
  cat "$summary"
  echo
fi

# "<group>\t<hash>\t<short subject>" per commit. The group is the text before the first ": " when it
# reads like a category (short, no comma); "a/b" counts as group a. Anything else goes under "other".
entries=$(git log --no-merges --format='%h%x09%s' "${args[@]}" | awk -F'\t' '
  {
    hash = $1; s = $2; group = "other"
    i = index(s, ": ")
    if (i > 1 && i <= 30) {
      g = substr(s, 1, i - 1)
      if (g !~ /,/) { group = tolower(g); sub(/\/.*/, "", group); s = substr(s, i + 2) }
    }
    n = split(", so |; | (", cuts, "|")
    for (k = 1; k <= n; k++) { j = index(s, cuts[k]); if (j > 1) s = substr(s, 1, j - 1) }
    if (length(s) > 120) { s = substr(s, 1, 117); sub(/ [^ ]*$/, "", s); s = s "…" }
    if (!seen[group "\t" s]++) print group "\t" hash "\t" s   # a commit picked onto two branches shows once
  }')

[ -n "$entries" ] || { echo "No commits since ${prev:-the start}." ; exit 0; }

# Groups largest first; "other" always last among the user-facing ones.
groups=$(cut -f1 <<<"$entries" | sort | uniq -c | sort -k1,1nr -k2 | awk '{ $1 = ""; sub(/^ /, ""); print }')

section() {  # <title> <groups regex>
  local title=$1 match=$2 body
  body=$(awk -F'\t' -v m="$match" '$1 ~ m { print "- " $2 " " $3 }' <<<"$entries")
  [ -n "$body" ] || return 0
  printf '<details>\n<summary><b>%s</b> (%d)</summary>\n\n%s\n\n</details>\n\n' \
    "$title" "$(wc -l <<<"$body")" "$body"
}

echo "## Changes since ${prev:-the start}"
echo
while IFS= read -r g; do
  [[ $g =~ $dev_groups || $g == other ]] && continue
  section "${g^}" "^${g}\$"
done <<<"$groups"
section Other '^other$'
section 'Developer: tests and tools' "$dev_groups"

cat <<'EOF'
*MacOS users must run the following command in Terminal after dragging MMUCS to their applications folder
codesign --force --deep --sign - /Applications/MMUCS.app
EOF
