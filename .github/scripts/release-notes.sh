#!/usr/bin/env bash
# Prints the GitHub release notes for a tag: a Full Changelog link to the
# previous tag, the tag's section of CHANGELOG.md, and the download table.
# Fails if CHANGELOG.md has no "## <tag>" section.
#   .github/scripts/release-notes.sh v2.0.3 > notes.md
set -euo pipefail

tag="${1:?usage: release-notes.sh <tag>}"
version="${tag#v}"
repo="${GITHUB_REPOSITORY:-0xDario/PunchClock}"
cd "$(dirname "$0")/../.."

# Lines after "## <tag>" (alone or followed by " - date") up to the next "## ",
# without leading or trailing blank lines.
section=$(awk -v h="## $tag" '
  { sub(/\r$/, "") }
  $0 == h || index($0, h " ") == 1 { on = 1; next }
  on && /^## / { exit }
  on { lines[++n] = $0 }
  END {
    first = 1; while (first <= n && lines[first] ~ /^[ \t]*$/) first++
    while (n >= first && lines[n] ~ /^[ \t]*$/) n--
    for (i = first; i <= n; i++) print lines[i]
  }' CHANGELOG.md)
if [ -z "$section" ]; then
  echo "CHANGELOG.md has no \"## $tag\" section. Add one with this release's changes before tagging." >&2
  exit 1
fi

# The previous release tag, when the tag and its history are present.
prev=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$tag^" 2>/dev/null || true)
if [ -n "$prev" ]; then
  printf '**Full Changelog**: https://github.com/%s/compare/%s...%s\n\n' "$repo" "$prev" "$tag"
fi

printf '%s\n\n' "$section"

cat <<NOTES
**Moving from the old PunchClock app (v1.x)?** Follow [the switchover steps in the README](https://github.com/$repo/blob/$tag/README.md#moving-from-the-old-punchclock-app) before anyone punches in the new app.

| File | Use |
|---|---|
| \`PunchClock-Setup-$version.exe\` | Installer. Includes the export and import tools for the switchover. |
| \`SHA256SUMS.txt\` | Checksum of the installer. |

The installer is not code-signed yet, so Windows SmartScreen asks for confirmation: click **More info**, then **Run anyway**.
NOTES
