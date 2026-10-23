#!/usr/bin/env python3
"""Prints libloot's sort of a real load order against the real, unstripped masterlist.

A user check for comparing the app's "Sort with LOOT" with libloot, not a test. Run it with the
interpreter that has the `loot` module (see README.md):

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/sort_real.py skyrimse my-load-order.txt

The input is a text file with one plugin per line, in the current load order:

    Skyrim.esm
    Update.esm
    SomeMod.esp :: Skyrim.esm, Update.esm
    # lines starting with # and blank lines are ignored

Names after `::` are the plugin's masters. Plugins ending in .esm or .esl get the master flag.
Each plugin becomes a header-only stub in a temp Data directory, so `file()` and `active()`-style
conditions in the masterlist see exactly these plugins and nothing else: every plugin counts as
present, and none is active unless the game's own rules make it so (conditions about versions, CRCs
and checksums cannot be met by stubs). That is the difference from what the app sees on the real
install; compare the order of the plugins both agree on.

The masterlist and prelude are the bundled ones under src/SilverModManager.Games.Bethesda.Loot/Masterlists
unless --masterlist or --prelude say otherwise. The sorted order is printed one name per line; an
error is printed to stderr with exit status 1.
"""

import argparse
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BUNDLED = os.path.join(HERE, "..", "..", "src", "SilverModManager.Games.Bethesda.Loot", "Masterlists")


def load_generator():
    # -I keeps the script's own directory off sys.path, so load the sibling module by path.
    spec = importlib.util.spec_from_file_location("loot_oracle_generate", os.path.join(HERE, "generate.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_load_order(path):
    plugins, seen = [], set()
    with open(path, encoding="utf-8") as f:
        for number, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            name, _, masters = line.partition("::")
            name = name.strip()
            if not name:
                raise SystemExit(f"{path}:{number}: no plugin name before '::'")
            if name.lower() in seen:
                raise SystemExit(f"{path}:{number}: {name} is listed twice")
            seen.add(name.lower())
            plugins.append((name, [m.strip() for m in masters.split(",") if m.strip()]))
    if not plugins:
        raise SystemExit(f"{path} lists no plugins")
    return plugins


def main():
    gen = load_generator()
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("game", choices=sorted(gen.TIER3_GAMES), help="bundled masterlist repository, e.g. skyrimse")
    parser.add_argument("load_order", help="text file: plugins in current order, masters after '::'")
    parser.add_argument("--masterlist", help="masterlist.yaml to use instead of the bundled one")
    parser.add_argument("--prelude", help="prelude.yaml to use instead of the bundled one")
    parser.add_argument("--userlist", help="userlist.yaml to apply on top")
    args = parser.parse_args()

    def read(path):
        with open(path, encoding="utf-8") as f:
            return f.read()

    plugins = [
        gen.P(name, masters, gen.MASTER if name.lower().endswith((".esm", ".esl")) else 0)
        for name, masters in read_load_order(args.load_order)
    ]
    case = dict(
        name="real", game_type=gen.TIER3_GAMES[args.game], plugins=plugins,
        masterlist=read(args.masterlist or os.path.join(BUNDLED, args.game, "masterlist.yaml")),
        prelude=read(args.prelude or os.path.join(BUNDLED, "prelude.yaml")),
        userlist=read(args.userlist) if args.userlist else None,
    )
    expected, _ = gen.run_case(case)
    if "error" in expected:
        print(f"{expected['error']['kind']}: {expected['error']['message']}", file=sys.stderr)
        return 1
    print("\n".join(expected["order"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
