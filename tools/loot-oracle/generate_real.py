#!/usr/bin/env python3
"""Generates the "real" LOOT oracle cases: load orders that came from real installs, sorted by libloot.

Two sources, one subcommand each. Both write
tests/Games/SilverModManager.Games.Bethesda.Loot.Tests/Corpus/real/<name>.json, in the same shape as the
tier 3 cases (stripped bundled masterlist and prelude, named by SHA-256), so the C# side loads them
with the code it already has. Run with the interpreter from README.md, in isolated mode:

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate_real.py wabbajack --allow-network \\
        --machine-url tempus_maledictum --download-dir <scratch> --stripped-dir <dir> --name tempus_maledictum
    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate_real.py headers \\
        --headers <plugin-headers.json> --repository oblivion-remastered --stripped-dir <dir> --name oblivion_remastered_instance

wabbajack: downloads only the .wabbajack file (the list's own zip of metadata; mods are never fetched),
reads the inline loadorder.txt of a profile and turns each entry into a header-only stub: ESM and
ESL flags from the extension (and the master flag for an .esp listed among the masters), the base
game's official masters as masters, no records. This is the only step that touches the network, so
it refuses to run without --allow-network, and nothing in the test suite calls it. The .wabbajack file stays in --download-dir; only plugin names and the list's
identity are committed.

headers: takes the JSON that the C# test WriteOblivionRemasteredHeadersForOracle writes (names, flags and
masters read with the app's own header reader from a Mod Organizer instance, read-only).

--stripped-dir is the directory the WriteStrippedMasterlistsForOracle test writes (see README.md).
"""

import argparse
import concurrent.futures
import gzip
import hashlib
import importlib.util
import json
import os
import re
import sys
import urllib.parse
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "tests", "Games", "SilverModManager.Games.Bethesda.Loot.Tests", "Corpus", "real")
LIST_INDEX = "https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/modlists.json"

SKYRIM_SE_OFFICIAL = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"]
ESL_FLAG = 0x200
PLUGIN_EXTENSIONS = (".esp", ".esm", ".esl")


def load_generator():
    # -I keeps this script's directory off sys.path, so load the sibling module by path.
    spec = importlib.util.spec_from_file_location("loot_oracle_generate", os.path.join(HERE, "generate.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "silvermodmanager-loot-oracle"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


# ------------------------------------------------------------------- wabbajack

def download_wabbajack(entry, directory):
    """Fetches the list's .wabbajack through the authored-files CDN's part definition."""
    os.makedirs(directory, exist_ok=True)
    # links.download is <base>/<Name>.wabbajack_<guid>; the CDN stores it as parts under that path.
    base = urllib.parse.quote(urllib.parse.unquote(entry["links"]["download"]), safe=":/")
    path = os.path.join(directory, entry["links"]["machineURL"] + ".wabbajack")
    definition = json.loads(gzip.decompress(fetch(base + "/definition.json.gz")))
    if os.path.exists(path) and os.path.getsize(path) == definition["Size"]:
        return path, definition

    def part(p):
        return p["Index"], fetch(f"{base}/parts/{p['Index']}")

    with open(path, "wb") as out, concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        for index, data in pool.map(part, definition["Parts"]):
            expected = definition["Parts"][index]["Size"]
            if len(data) != expected:
                raise SystemExit(f"part {index} of {path} is {len(data)} bytes, expected {expected}")
            out.write(data)
    if os.path.getsize(path) != definition["Size"]:
        raise SystemExit(f"{path} is {os.path.getsize(path)} bytes, the definition says {definition['Size']}")
    return path, definition


def profile_inline_files(wabbajack_path, profile):
    """The profile's inline files by name, e.g. 'loadorder.txt' -> bytes."""
    with zipfile.ZipFile(wabbajack_path) as z:
        modlist = json.loads(z.read("modlist"))
        files = {}
        prefix = f"profiles\\{profile}\\"
        for directive in modlist["Directives"]:
            if directive.get("$type") != "InlineFile":
                continue
            to = directive["To"].replace("/", "\\")
            if to.lower().startswith(prefix.lower()):
                files[to[len(prefix):]] = z.read(directive["SourceDataID"])
        profiles = sorted({
            d["To"].replace("/", "\\").split("\\")[1] for d in modlist["Directives"]
            if d.get("$type") == "InlineFile" and d["To"].replace("/", "\\").lower().startswith("profiles\\")
            and d["To"].count("\\") + d["To"].count("/") >= 2})
        return modlist, files, profiles


def read_plugin_list(data):
    """Plugin names from a loadorder.txt, in order, without comments and repeats."""
    names, seen = [], set()
    for raw in data.decode("utf-8", errors="replace").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or not line.lower().endswith(PLUGIN_EXTENSIONS):
            continue
        if line.lower() not in seen:
            seen.add(line.lower())
            names.append(line)
    return names


def is_creation_club(name):
    return re.match(r"(?i)^cc[a-z0-9]+-", name) is not None


def skyrim_se_stubs(gen, names):
    """
    One header-only stub per name. The base game's plugins master the ones before them; Creation Club
    plugins master the base game's; every other plugin masters the base game's and the Creation Club
    .esm files the list enables, which is what a mod built against the full game looks like.
    """
    present = {n.lower() for n in names}
    # A profile's loadorder.txt may leave out the base game's own plugins; the game always loads them first.
    names = [o for o in SKYRIM_SE_OFFICIAL if o.lower() not in present] + names
    official = {o.lower(): i for i, o in enumerate(SKYRIM_SE_OFFICIAL)}
    club_masters = [n for n in names if is_creation_club(n) and n.lower().endswith(".esm")]
    # The extension is not the whole story: an .esp can carry the master flag (the unofficial patches
    # do), and a name cannot say so. The game loads every master-flagged plugin before any other, so
    # an .esp listed ahead of the last .esm or .esl must have been flagged.
    last_master = max((i for i, n in enumerate(names) if n.lower().endswith((".esm", ".esl"))), default=-1)

    stubs = []
    for index, name in enumerate(names):
        lower = name.lower()
        flags = gen.MASTER if lower.endswith((".esm", ".esl")) or index < last_master else 0
        if lower.endswith(".esl"):
            flags |= ESL_FLAG
        if lower in official:
            masters = SKYRIM_SE_OFFICIAL[:official[lower]]
        elif is_creation_club(name):
            masters = list(SKYRIM_SE_OFFICIAL)
        else:
            masters = SKYRIM_SE_OFFICIAL + club_masters
        stubs.append(gen.P(name, masters, flags))
    return stubs


# ----------------------------------------------------------------------- cases

def real_case(gen, name, repository, stubs, stripped_dir, source):
    with open(os.path.join(stripped_dir, "sha256.json"), encoding="utf-8") as f:
        hashes = json.load(f)
    case = gen.real_masterlist_case(repository, 0, stripped_dir, hashes, 0.0)
    case.update(name=name, tier="real", seed=None, game_type=gen.TIER3_GAMES[repository], plugins=stubs, source=source)
    return case


def write_case(gen, case, out):
    expected, paths = gen.run_case(case)
    elapsed_ms = gen.LAST_SORT_SECONDS * 1000
    if "order" not in expected:
        raise SystemExit(f"{case['name']}: libloot did not sort it: {expected['error']['kind']}: {expected['error']['message']}")
    import loot

    game = gen.GAMES[case["game_type"]]
    fixture = {
        "libloot_version": loot.libloot_version(),
        "libloot_revision": loot.libloot_revision(),
        "generator_version": gen.GENERATOR_VERSION,
        "seed": None,
        "game_type": case["game_type"],
        "format": game["format"],
        "header_version": game["version"],
        "name": case["name"],
        "source": case["source"],
        "plugins": case["plugins"],
        "masterlist": None,
        "userlist": None,
        "prelude": None,
        "real_masterlist": case["real_masterlist"],
        # The sort_plugins call alone, one run, on the machine that generated the case. Informational:
        # the test logs its own timing beside it.
        "oracle_sort_ms": round(elapsed_ms),
        "expected": expected,
        "groups_paths": paths,
    }
    os.makedirs(out, exist_ok=True)
    target = os.path.join(out, case["name"] + ".json")
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        f.write(gen.render(fixture) + "\n")
    print(f"{case['name']}: {len(case['plugins'])} plugins, libloot sort {elapsed_ms:.0f} ms -> {target}")


def cmd_wabbajack(args, gen):
    if not args.allow_network:
        raise SystemExit("this step downloads a .wabbajack file; pass --allow-network to say that is intended")
    index = json.loads(fetch(LIST_INDEX))
    entry = next((m for m in index if m["links"]["machineURL"] == args.machine_url), None)
    if entry is None:
        raise SystemExit(f"{args.machine_url} is not in {LIST_INDEX}")
    if entry["game"] != "skyrimspecialedition":
        raise SystemExit(f"{args.machine_url} is a {entry['game']} list; only Skyrim SE stubs are built here")

    path, definition = download_wabbajack(entry, args.download_dir)
    with open(path, "rb") as f:
        sha256 = hashlib.file_digest(f, "sha256").hexdigest()
    modlist, files, profiles = profile_inline_files(path, args.profile or "")
    if not files:
        raise SystemExit(f"no inline files under profiles\\{args.profile}\\; the list has profiles: {profiles}")

    names = read_plugin_list(files["loadorder.txt"])
    stubs = skyrim_se_stubs(gen, names)
    source = {
        "kind": "wabbajack",
        "machine_name": entry["links"]["machineURL"],
        "title": entry["title"],
        "version": modlist.get("Version", entry["version"]),
        "profile": args.profile,
        "wabbajack_xxhash64": definition["Hash"],
        "wabbajack_sha256": sha256,
        "wabbajack_size": definition["Size"],
        "loadorder_entries": len(names),
    }
    case = real_case(gen, args.name, "skyrimse", stubs, args.stripped_dir, source)
    write_case(gen, case, args.out)


def cmd_headers(args, gen):
    with open(args.headers, encoding="utf-8") as f:
        headers = json.load(f)
    stubs = [gen.P(h["name"], h["masters"], h["flags"]) for h in headers["plugins"]]
    source = {k: v for k, v in headers.items() if k != "plugins"}
    case = real_case(gen, args.name, args.repository, stubs, args.stripped_dir, source)
    write_case(gen, case, args.out)


def main():
    gen = load_generator()
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--stripped-dir", required=True, help="directory written by WriteStrippedMasterlistsForOracle")
    common.add_argument("--name", required=True, help="case name, also the file name")
    common.add_argument("--out", default=OUT, help="directory the case is written to")

    w = sub.add_parser("wabbajack", parents=[common], help="a Skyrim SE Wabbajack list's load order")
    w.add_argument("--machine-url", required=True)
    w.add_argument("--profile", required=True, help="profile folder under profiles\\ in the list")
    w.add_argument("--download-dir", required=True, help="where the .wabbajack file is kept; outside the repo")
    w.add_argument("--allow-network", action="store_true")
    w.set_defaults(run=cmd_wabbajack)

    h = sub.add_parser("headers", parents=[common], help="plugin headers read from a real install")
    h.add_argument("--headers", required=True)
    h.add_argument("--repository", required=True, choices=sorted(gen.TIER3_GAMES))
    h.set_defaults(run=cmd_headers)

    args = parser.parse_args()
    args.run(args, gen)


if __name__ == "__main__":
    sys.exit(main())
