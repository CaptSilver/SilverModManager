#!/usr/bin/env python3
"""Sorts the plugins of a real, installed game with libloot, records and archives included.

A user check, not a test, and the counterpart of the opt-in `InstalledLoadOrderComparisonTests`: both
sort the same plugin list from the same game folder, then the orders are compared. Unlike sort_real.py
the plugins are the real files, so libloot reads their records and the .bsa/.ba2 archives they load and
adds overlap edges. The game folder is only read. Run it with the interpreter that has the `loot`
module (see README.md):

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/sort_installed.py skyrimse \\
        --game-path ~/.local/share/Steam/steamapps/common/SkyrimVR --game-type SkyrimVR \\
        --plugins-txt <plugins.txt> --implicit Skyrim.esm,Update.esm,Dawnguard.esm,HearthFires.esm,Dragonborn.esm,SkyrimVR.esm \\
        --stripped-dir "$LOOT_ORACLE_STRIP_DIR" --out result.json

The sorted plugins are `--implicit` followed by the `*`-marked lines of plugins.txt, in file order.
The masterlist is the bundled one for the repository named first, with its conditions removed when
`--stripped-dir` is given (StrippedMasterlistExportTests writes them when LOOT_ORACLE_STRIP_DIR is set), which is what the C#
side sorts with. Without it the real conditions run against the real folder.

The result is JSON: the order, how long loading the plugins and sorting took, and each plugin's
master flag, masters and archive loading, so a difference in the input can be told from one in the sort.
"""

import argparse
import json
import os
import shutil
import sys
import tempfile
import time

import loot

HERE = os.path.dirname(os.path.abspath(__file__))
BUNDLED = os.path.join(HERE, "..", "..", "src", "SilverModManager.Games.Bethesda.Loot", "Masterlists")


def read_active(path):
    names = []
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if line.startswith("*"):
                names.append(line[1:])
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("repository", help="bundled masterlist repository, e.g. skyrimse")
    parser.add_argument("--game-path", required=True, help="the game folder (it holds Data/)")
    parser.add_argument("--game-type", required=True, choices=[n for n in dir(loot.GameType) if n[0].isupper()])
    parser.add_argument("--plugins-txt", required=True, help="the active plugin list, '*' marks an active plugin")
    parser.add_argument("--implicit", default="", help="comma-separated plugins that come first and are not in plugins.txt")
    parser.add_argument("--stripped-dir", help="directory of condition-free masterlists written by the C# export test")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    names = [n for n in args.implicit.split(",") if n]
    seen = {n.lower() for n in names}
    for name in read_active(args.plugins_txt):
        if name.lower() not in seen:
            seen.add(name.lower())
            names.append(name)

    if args.stripped_dir:
        masterlist = os.path.join(args.stripped_dir, f"{args.repository}.masterlist.yaml")
        prelude = os.path.join(args.stripped_dir, "prelude.yaml")
    else:
        masterlist = os.path.join(BUNDLED, args.repository, "masterlist.yaml")
        prelude = os.path.join(BUNDLED, "prelude.yaml")

    local = tempfile.mkdtemp(prefix="loot-installed-")
    try:
        shutil.copyfile(args.plugins_txt, os.path.join(local, "plugins.txt"))
        game = loot.Game(getattr(loot.GameType, args.game_type), args.game_path, local)
        game.database().load_masterlist_with_prelude(masterlist, prelude)
        game.load_current_load_order_state()

        data = os.path.join(args.game_path, "Data")
        started = time.perf_counter()
        game.load_plugins([os.path.join(data, n) for n in names])
        load_seconds = time.perf_counter() - started

        started = time.perf_counter()
        try:
            result = {"order": game.sort_plugins(names)}
        except Exception as e:
            result = {"error": f"{type(e).__name__}: {e}"}
        sort_seconds = time.perf_counter() - started

        plugins = []
        for name in names:
            p = game.plugin(name)
            plugins.append({
                "name": name, "is_master": p.is_master(), "is_blueprint": p.is_blueprint_plugin(),
                "masters": p.masters(), "loads_archive": p.loads_archive(),
            })
        result.update(game_type=args.game_type, input=names, plugins=plugins,
                      libloot_version=loot.libloot_version(), libloot_revision=loot.libloot_revision(),
                      load_plugins_ms=round(load_seconds * 1000), sort_plugins_ms=round(sort_seconds * 1000),
                      stripped=bool(args.stripped_dir))
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=1)
            f.write("\n")
        print(f"{len(names)} plugins; load {load_seconds * 1000:.0f} ms, sort {sort_seconds * 1000:.0f} ms; "
              + (result["error"] if "error" in result else "sorted"))
        return 1 if "error" in result else 0
    finally:
        shutil.rmtree(local, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
