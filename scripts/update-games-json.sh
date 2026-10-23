#!/usr/bin/env bash
# Refreshes the vendored games.json embedded in SilverModManager.Networking.NexusWebApi.
# The build never downloads it, so run this when you want newer game ids and
# commit the result. Only id and domain_name are kept, the rest of the upstream
# file (download counts etc.) changes constantly and nothing reads it.
# GAMES_JSON_URL overrides the source (a file:// URL works).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
url="${GAMES_JSON_URL:-https://data.nexusmods.com/file/nexus-data/games.json}"
target="$root/src/SilverModManager.Networking.NexusWebApi/V1Interop/games.json"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
raw="$work/raw.json"
tmp="$work/games.json"

# -f makes curl fail on HTTP errors instead of saving the error page.
curl -fsSL -o "$raw" "$url"

# Sorted by id with one entry per line, so refreshes produce readable diffs.
jq -c 'sort_by(.id) | .[] | {id, domain_name}' "$raw" | jq -s -c . | sed 's/},{/},\n{/g' > "$tmp"

if [[ "$(jq length "$tmp")" -eq 0 ]]; then
    echo "games.json came back empty, keeping the existing file" >&2
    exit 1
fi

mv "$tmp" "$target"
echo "Wrote $(jq length "$target") entries to $target"
