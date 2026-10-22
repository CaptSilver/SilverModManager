#!/usr/bin/env bash
# Downloads the LOOT masterlists and the shared prelude into the Loot project and rewrites
# masterlists.lock.json. Run it by hand when the bundle should move; the files are committed.
set -euo pipefail

BRANCH="v0.29"
REPOS=(morrowind oblivion oblivion-remastered skyrim skyrimse fallout3 falloutnv fallout4 starfield enderal)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="$root/src/NexusMods.Games.Bethesda.Loot/Masterlists"
mkdir -p "$out"

date_utc="$(date -u +%Y-%m-%d)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

entries=()

# fetch <repo> <remote file> <local relative path>
fetch() {
    local repo="$1" remote="$2" rel="$3"
    local url="https://raw.githubusercontent.com/loot/$repo/$BRANCH/$remote"
    local dest="$tmp/$(echo "$rel" | tr / _)"

    # -f makes a 404 (a repo without this branch) fatal instead of saving the error page.
    curl -fsSL -o "$dest" "$url" || { echo "FAILED: $url" >&2; exit 1; }

    local sha
    sha="$(curl -fsSL "https://api.github.com/repos/loot/$repo/commits/$BRANCH" \
        | python3 -c 'import json,sys; print(json.load(sys.stdin)["sha"])')" \
        || { echo "FAILED: commit lookup for loot/$repo" >&2; exit 1; }

    local hash
    hash="$(sha256sum "$dest" | cut -d' ' -f1)"
    entries+=("$rel|$repo|$sha|$hash")
    echo "$rel  $sha  $hash"
}

for repo in "${REPOS[@]}"; do
    fetch "$repo" masterlist.yaml "$repo/masterlist.yaml"
done
fetch prelude prelude.yaml prelude.yaml

# Only replace the bundle once every download succeeded.
for e in "${entries[@]}"; do
    rel="${e%%|*}"
    mkdir -p "$out/$(dirname "$rel")"
    cp "$tmp/$(echo "$rel" | tr / _)" "$out/$rel"
done

python3 - "$out/masterlists.lock.json" "$BRANCH" "$date_utc" "${entries[@]}" <<'PY'
import json, sys
path, branch, date = sys.argv[1:4]
files = []
for e in sys.argv[4:]:
    rel, repo, sha, digest = e.split("|")
    files.append({"file": rel, "repo": repo, "branch": branch, "commit": sha, "sha256": digest, "retrieved": date})
with open(path, "w") as f:
    json.dump({"files": files}, f, indent=2)
    f.write("\n")
PY
