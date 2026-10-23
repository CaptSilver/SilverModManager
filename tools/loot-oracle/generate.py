#!/usr/bin/env python3
"""Generates the LOOT oracle corpus: sort results from libloot's own Python binding.

Run with the interpreter that has the `loot` module in its site-packages, in isolated mode:

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py

Each case writes synthetic plugins (headers only) into a temp Data directory, loads the case's
masterlist, userlist and prelude, sorts, and stores the expected order or the error. The JSON goes
under tests/Games/NexusMods.Games.Bethesda.Loot.Tests/Corpus/{tier1,tier2,tier3}/. Only the standard
library and `loot` are used, because that interpreter has nothing else.

Tier 3 sorts synthetic plugins named after the entries of the real, condition-stripped LOOT
masterlists. The stripped files come from the C# test WriteStrippedMasterlistsForOracle (run it with
LOOT_ORACLE_STRIP_DIR set) and are passed with --stripped-dir; they are never committed. A tier 3 case
keeps the SHA-256 of each bundled file and its stripped form instead of the text.

The plugin byte layout matches PluginBytes in the LoadOrder test project.
"""

import argparse
import hashlib
import json
import os
import random
import re
import shutil
import struct
import sys
import tempfile
import time

import loot

GENERATOR_VERSION = "1"
TIER2_SEEDS = 40
TIER3_SEEDS = 4

MASTER = 0x1
SF_LIGHT = 0x100
SF_UPDATE = 0x200
SF_MEDIUM = 0x400
SF_BLUEPRINT = 0x800

# The format name is a PluginFormat value on the C# side.
GAMES = {
    "SkyrimSE": dict(data="Data", master="Skyrim.esm", format="Tes4", version=1.7),
    "Oblivion": dict(data="Data", master="Oblivion.esm", format="Tes4Oblivion", version=1.0),
    "Morrowind": dict(data="Data Files", master="Morrowind.esm", format="Tes3", version=1.3),
    "Starfield": dict(data="Data", master="Starfield.esm", format="Tes4", version=0.96),
    "Fallout4": dict(data="Data", master="Fallout4.esm", format="Tes4", version=1.0),
    "Skyrim": dict(data="Data", master="Skyrim.esm", format="Tes4", version=0.94),
    "Fallout3": dict(data="Data", master="Fallout3.esm", format="Tes4", version=0.94),
    "FalloutNV": dict(data="Data", master="FalloutNV.esm", format="Tes4", version=1.34),
    "OblivionRemastered": dict(
        data="OblivionRemastered/Content/Dev/ObvData/Data", master="Oblivion.esm", format="Tes4Oblivion", version=1.0),
}

# Bundled masterlist repository -> the binding's game type. The binding has no Enderal type, and
# Enderal SE runs on the Skyrim SE rules.
TIER3_GAMES = {
    "skyrimse": "SkyrimSE",
    "skyrim": "Skyrim",
    "oblivion": "Oblivion",
    "oblivion-remastered": "OblivionRemastered",
    "fallout3": "Fallout3",
    "falloutnv": "FalloutNV",
    "fallout4": "Fallout4",
    "starfield": "Starfield",
    "morrowind": "Morrowind",
    "enderal": "SkyrimSE",
}


def P(name, masters=(), flags=0):
    return {"name": name, "flags": flags, "masters": list(masters)}


def M(name, masters=(), extra=0):
    return P(name, masters, MASTER | extra)


# ---------------------------------------------------------------- plugin bytes

def _sub(kind, data, wide):
    return kind + struct.pack("<I" if wide else "<H", len(data)) + data


def _cstring(text):
    return text.encode("cp1252") + b"\0"


def write_plugin(path, fmt, spec, version):
    wide = fmt == "Tes3"
    flags = spec["flags"]
    body = b""
    if fmt == "Tes3":
        hedr = bytearray(300)
        struct.pack_into("<fI", hedr, 0, version, flags)
        body += _sub(b"HEDR", bytes(hedr), True)
    else:
        body += _sub(b"HEDR", struct.pack("<fII", version, 0, 0), False)
    for master in spec["masters"]:
        body += _sub(b"MAST", _cstring(master), wide) + _sub(b"DATA", bytes(8), wide)

    if fmt == "Tes3":
        data = b"TES3" + struct.pack("<I", len(body)) + bytes(4) + struct.pack("<I", 0) + body
    else:
        pad = 4 if fmt == "Tes4Oblivion" else 8
        data = b"TES4" + struct.pack("<III", len(body), flags, 0) + bytes(pad) + body
    with open(path, "wb") as f:
        f.write(data)


# ---------------------------------------------------------------------- running

def edge_name(edge_type):
    return None if edge_type is None else str(edge_type).split(".")[-1]


def vertices(path):
    return [[v.name, edge_name(v.out_edge_type)] for v in path]


def error_record(error):
    record = {"kind": type(error).__name__, "message": str(error)}
    if isinstance(error, loot.CyclicInteractionError):
        cycle, text = error.args
        record["cycle"] = vertices(cycle)
        record["cycle_text"] = text.removeprefix("found a cycle: ")
    return record


# How long the last sort_plugins call took; generate_real.py reads it, the corpus files do not hold it.
LAST_SORT_SECONDS = 0.0


def run_case(case):
    game = GAMES[case["game_type"]]
    root = tempfile.mkdtemp(prefix="loot-oracle-")
    try:
        data = os.path.join(root, game["data"])
        local = os.path.join(root, "local")
        os.makedirs(data)
        os.makedirs(local)
        if case["game_type"] == "Morrowind":
            with open(os.path.join(root, "Morrowind.ini"), "w") as f:
                f.write("[Game Files]\nGameFile0=Morrowind.esm\n")

        names = [p["name"] for p in case["plugins"]]
        if game["master"].lower() not in (n.lower() for n in names):
            write_plugin(os.path.join(data, game["master"]), game["format"], M(game["master"]), game["version"])
        for spec in case["plugins"]:
            write_plugin(os.path.join(data, spec["name"]), game["format"], spec, game["version"])

        g = loot.Game(getattr(loot.GameType, case["game_type"]), root, local)
        database = g.database()

        if case.get("masterlist") is not None:
            masterlist = os.path.join(root, "masterlist.yaml")
            with open(masterlist, "w", encoding="utf-8") as f:
                f.write(case["masterlist"])
            if case.get("prelude") is not None:
                prelude = os.path.join(root, "prelude.yaml")
                with open(prelude, "w", encoding="utf-8") as f:
                    f.write(case["prelude"])
                database.load_masterlist_with_prelude(masterlist, prelude)
            else:
                database.load_masterlist(masterlist)
        if case.get("userlist") is not None:
            userlist = os.path.join(root, "userlist.yaml")
            with open(userlist, "w", encoding="utf-8") as f:
                f.write(case["userlist"])
            database.load_userlist(userlist)

        g.load_current_load_order_state()
        # Headers are all a record-less plugin has, and load_plugins would refuse a missing master on
        # Morrowind, OpenMW and Starfield.
        g.load_plugin_headers([os.path.join(data, n) for n in names])

        def sort(order):
            global LAST_SORT_SECONDS
            started = time.perf_counter()
            try:
                return {"order": g.sort_plugins(order)}
            except Exception as e:  # the binding raises its own types; the record keeps the type name
                return {"error": error_record(e)}
            finally:
                LAST_SORT_SECONDS = time.perf_counter() - started

        expected = sort(names)
        if case.get("resort") and "order" in expected:
            expected["resorted"] = sort(expected["order"])["order"]

        paths = []
        for from_group, to_group in case.get("groups_paths", []):
            entry = {"from": from_group, "to": to_group}
            try:
                entry["path"] = vertices(database.groups_path(from_group, to_group))
            except Exception as e:
                entry["error"] = error_record(e)
            paths.append(entry)
        return expected, paths
    finally:
        shutil.rmtree(root, ignore_errors=True)


# --------------------------------------------------------------- JSON rendering

def _compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(", ", ": "))


def render(value, indent=0):
    """Pretty JSON that keeps short lists and objects on one line, so a plugin is one line."""
    pad = "  " * (indent + 1)
    end = "  " * indent
    if isinstance(value, dict):
        flat = _compact(value)
        if len(flat) <= 100 and "\\n" not in flat:
            return flat
        items = [f"{pad}{json.dumps(k)}: {render(v, indent + 1)}" for k, v in value.items()]
        return "{\n" + ",\n".join(items) + "\n" + end + "}"
    if isinstance(value, list):
        flat = _compact(value)
        if len(flat) <= 100 and "\\n" not in flat:
            return flat
        return "[\n" + ",\n".join(pad + render(v, indent + 1) for v in value) + "\n" + end + "]"
    return json.dumps(value, ensure_ascii=False)


# ---------------------------------------------------------------------- tier 1

def groups_yaml(groups):
    """groups: list of (name, [after...])."""
    lines = ["groups:"]
    for name, after in groups:
        lines.append(f"  - name: {name}")
        if after:
            lines.append(f"    after: [{', '.join(after)}]")
    return "\n".join(lines) + "\n"


def tier1_cases():
    cases = []

    def add(name, game_type, plugins, **kw):
        cases.append(dict(name=name, tier=1, game_type=game_type, plugins=plugins, **kw))

    sse = "SkyrimSE"

    # One case per EdgeType that the sort can leave visible in the order. The current order is the
    # reverse of what the edge asks for, so the edge is what moves the plugin.
    add("edge_master", sse, [P("A.esp", ["B.esp"]), P("B.esp"), P("C.esp")])
    add("edge_masterlist_requirement", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist="plugins:\n  - name: A.esp\n    req: [B.esp]\n")
    add("edge_user_requirement", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        userlist="plugins:\n  - name: A.esp\n    req: [B.esp]\n")
    add("edge_masterlist_load_after", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist="plugins:\n  - name: A.esp\n    after: [C.esp]\n")
    add("edge_user_load_after", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        userlist="plugins:\n  - name: A.esp\n    after: [C.esp]\n")
    add("edge_masterlist_group", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist=groups_yaml([("late", ["default"])]) + "plugins:\n  - name: A.esp\n    group: late\n")
    add("edge_user_group", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        userlist=groups_yaml([("late", ["default"])]) + "plugins:\n  - name: A.esp\n    group: late\n")
    add("edge_hardcoded_early_loaders", sse, [
        M("Dragonborn.esm"), M("Mod.esm"), P("Plain.esp"), M("Skyrim.esm"), M("HearthFires.esm"),
        M("Update.esm"), P("Other.esp"), M("Dawnguard.esm"),
    ])
    add("edge_tie_break", sse, [P("C.esp"), P("A.esp"), P("B.esp"), P("D.esp")])
    add("edge_tie_break_around_an_edge", sse, [P("C.esp"), P("A.esp"), P("B.esp"), P("D.esp"), P("E.esp")],
        masterlist="plugins:\n  - name: C.esp\n    after: [E.esp]\n")
    add("edge_master_flag_partition", sse, [
        P("A.esp"), M("M.esm"), P("B.esp"), M("N.esm", ["M.esm"]), P("C.esp", ["N.esm"]),
        # The .esm extension makes a plugin a master on Skyrim SE whatever its flags say.
        P("Flagless.esm"), P("Flagged.esp", flags=MASTER),
    ])
    add("master_listed_after_a_non_master", sse, [P("N.esp"), M("M.esm")],
        masterlist="plugins:\n  - name: M.esm\n    after: [N.esp]\n")

    # Cycles, with the text libloot prints.
    add("cycle_masterlist_load_after", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist="plugins:\n  - name: A.esp\n    after: [B.esp]\n  - name: B.esp\n    after: [C.esp]\n"
                   "  - name: C.esp\n    after: [A.esp]\n")
    add("cycle_master_and_masterlist_load_after", sse, [P("A.esp", ["B.esp"]), P("B.esp")],
        masterlist="plugins:\n  - name: B.esp\n    after: [A.esp]\n")
    add("cycle_user_load_after_and_requirement", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist="plugins:\n  - name: A.esp\n    req: [B.esp]\n",
        userlist="plugins:\n  - name: B.esp\n    after: [A.esp]\n")
    add("cycle_user_requirement", sse, [P("A.esp"), P("B.esp")],
        userlist="plugins:\n  - name: A.esp\n    req: [B.esp]\n  - name: B.esp\n    req: [A.esp]\n")
    add("cycle_in_user_groups", sse, [P("A.esp"), P("B.esp")],
        masterlist=groups_yaml([("g1", ["default"]), ("g2", ["g1"])]),
        userlist=groups_yaml([("g1", ["g2"])]),
        groups_paths=[["default", "g2"]])
    add("undefined_group", sse, [P("A.esp")], masterlist="plugins:\n  - name: A.esp\n    group: nowhere\n")
    add("cycle_between_early_loader_and_load_after", sse, [M("Skyrim.esm"), M("Dragonborn.esm"), M("Mod.esm")],
        masterlist="plugins:\n  - name: Skyrim.esm\n    after: [Mod.esm]\n")

    # Group precedence.
    group_defs = [("early", []), ("mid", ["early"]), ("late", ["mid"])]
    add("group_user_overrides_masterlist", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist=groups_yaml(group_defs)
        + "plugins:\n  - name: A.esp\n    group: late\n  - name: B.esp\n    group: early\n  - name: C.esp\n    group: mid\n",
        userlist="plugins:\n  - name: A.esp\n    group: early\n  - name: B.esp\n    group: late\n")
    add("group_user_group_extends_masterlist_group", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist=groups_yaml([("g1", ["default"]), ("g2", [])])
        + "plugins:\n  - name: A.esp\n    group: g1\n  - name: B.esp\n    group: g2\n",
        userlist=groups_yaml([("g2", ["g1"])]),
        groups_paths=[["g1", "g2"], ["default", "g2"]])
    add("group_default_after_other", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist=groups_yaml([("first", []), ("default", ["first"]), ("last", ["default"])])
        + "plugins:\n  - name: A.esp\n    group: last\n  - name: C.esp\n    group: first\n")

    # Two group paths of the same weight: the Bellman-Ford relaxation order picks one.
    diamond = [("a", ["default"]), ("b", ["default"]), ("c", ["default"]), ("d", ["a", "b", "c"])]
    add("group_path_tie_diamond", sse, [P("A.esp"), P("B.esp"), P("D.esp")],
        masterlist=groups_yaml(diamond) + "plugins:\n  - name: D.esp\n    group: d\n  - name: A.esp\n    group: a\n",
        groups_paths=[["default", "d"], ["a", "d"], ["default", "a"]])
    wide = [("x1", ["default"]), ("x2", ["default"]), ("y1", ["x1"]), ("y2", ["x2"]), ("z", ["y1", "y2"]), ("w", ["z", "x1"])]
    add("group_path_tie_two_levels", sse, [P("A.esp"), P("B.esp"), P("C.esp"), P("D.esp")],
        masterlist=groups_yaml(wide)
        + "plugins:\n  - name: A.esp\n    group: w\n  - name: B.esp\n    group: z\n  - name: C.esp\n    group: x2\n",
        groups_paths=[["default", "w"], ["default", "z"], ["x1", "w"]])
    add("group_path_prefers_user_edges", sse, [P("A.esp"), P("B.esp")],
        masterlist=groups_yaml([("a", ["default"]), ("b", ["default"]), ("d", ["a", "b"])]),
        userlist=groups_yaml([("d", ["b"])]),
        groups_paths=[["default", "d"]])

    # Several paths between consecutive plugins: the bidirectional BFS order decides which nodes the
    # tie-break pins.
    add("tie_break_diamond", sse, [P("A.esp"), P("C.esp"), P("B.esp"), P("D.esp")],
        masterlist="plugins:\n  - name: B.esp\n    after: [A.esp]\n  - name: C.esp\n    after: [A.esp]\n"
                   "  - name: D.esp\n    after: [B.esp, C.esp]\n")
    add("tie_break_diamond_edges_in_other_order", sse, [P("D.esp"), P("C.esp"), P("B.esp"), P("A.esp")],
        masterlist="plugins:\n  - name: D.esp\n    after: [C.esp, B.esp]\n  - name: C.esp\n    after: [A.esp]\n"
                   "  - name: B.esp\n    after: [A.esp]\n")
    add("tie_break_mixed_edge_types", sse, [P("E.esp"), P("D.esp"), P("C.esp"), P("B.esp"), P("A.esp")],
        masterlist="plugins:\n  - name: B.esp\n    after: [A.esp]\n  - name: C.esp\n    req: [A.esp]\n",
        userlist="plugins:\n  - name: D.esp\n    after: [B.esp]\n    req: [C.esp]\n")
    add("tie_break_ladder", sse, [P(f"P{i}.esp") for i in (5, 2, 7, 1, 6, 3, 4)],
        masterlist="plugins:\n  - name: P3.esp\n    after: [P1.esp, P2.esp]\n  - name: P4.esp\n    after: [P3.esp, P5.esp]\n"
                   "  - name: P6.esp\n    after: [P4.esp]\n  - name: P7.esp\n    after: [P5.esp, P6.esp]\n")

    # Few constraints and many valid orders.
    add("toposort_many_valid_orders", sse, [P(f"N{i}.esp") for i in (8, 3, 6, 1, 9, 4, 7, 2, 5)],
        masterlist="plugins:\n  - name: N1.esp\n    after: [N9.esp]\n  - name: N2.esp\n    after: [N8.esp]\n"
                   "  - name: N4.esp\n    after: [N7.esp]\n")

    # Metadata names.
    add("regex_name", sse, [P("Alpha1.esp"), P("Alpha22.esp"), P("Beta.esp"), P("Target.esp")],
        masterlist="plugins:\n  - name: 'Alpha\\d\\.esp'\n    after: [Target.esp]\n")
    # A name with a bar is wrapped as ^name$, so the alternatives are anchored at one end each:
    # Foo.esp matches the first, XBar.esp the second, Bar.esp both, and Other.esp neither.
    add("regex_name_alternation", sse, [P("Other.esp"), P("Foo.esp"), P("XBar.esp"), P("Bar.esp"), P("Target.esp")],
        masterlist="plugins:\n  - name: 'Foo|Bar\\.esp'\n    after: [Target.esp]\n")
    add("regex_name_in_userlist_and_masterlist", sse, [P("Alpha1.esp"), P("Beta.esp"), P("Target.esp"), P("Mid.esp")],
        masterlist="plugins:\n  - name: 'Alpha\\d\\.esp'\n    after: [Target.esp]\n  - name: Alpha1.esp\n    after: [Mid.esp]\n",
        userlist="plugins:\n  - name: 'Be.*\\.esp'\n    req: [Mid.esp]\n")
    add("merge_key", sse, [P("A.esp"), P("B.esp"), P("C.esp"), P("D.esp")],
        masterlist="common:\n  - &afterD\n    after: [D.esp]\n  - &groupLate\n    group: late\n"
                   + groups_yaml([("late", ["default"])])
                   + "plugins:\n  - name: A.esp\n    <<: *afterD\n  - name: B.esp\n    <<: [*afterD, *groupLate]\n"
                   "  - name: C.esp\n    <<: *afterD\n    after: [B.esp]\n")
    add("prelude_anchor", sse, [P("A.esp"), P("B.esp"), P("C.esp")],
        masterlist="prelude:\n  common:\n    - &needsB\n      after: [Wrong.esp]\n\n"
                   "plugins:\n  - name: A.esp\n    <<: *needsB\n  - name: C.esp\n    <<: *needsB\n",
        prelude="common:\n  - &needsB\n    after: [B.esp]\n")

    # Starfield.
    sf = "Starfield"
    add("starfield_blueprint_partition", sf, [
        P("Plain.esp"), M("Blue.esm", extra=SF_BLUEPRINT), M("Base.esm"), P("Other.esp"),
        M("BlueTwo.esm", ["Blue.esm"], extra=SF_BLUEPRINT), M("Starfield.esm"),
    ])
    add("starfield_flags", sf, [
        P("Light.esp", flags=SF_LIGHT), P("Medium.esp", flags=SF_MEDIUM), P("Update.esp", ["Medium.esp"], flags=SF_UPDATE),
        M("LightMaster.esm", extra=SF_LIGHT), P("Both.esp", flags=SF_LIGHT | SF_MEDIUM),
        # The extension makes it a master on Starfield even without the master flag.
        P("ByExtension.esm"), P("BlueprintNotMaster.esp", flags=SF_BLUEPRINT),
        M("BlueprintLight.esm", extra=SF_BLUEPRINT | SF_LIGHT),
    ])
    add("starfield_missing_master", sf, [P("A.esp", ["Gone.esm"]), P("B.esp", ["A.esp"]), M("C.esm", ["AlsoGone.esm"])])
    add("starfield_early_loaders", sf, [
        M("SFBGS003.esm"), M("Mod.esm"), M("Constellation.esm"), P("Plain.esp"), M("OldMars.esm"), M("Starfield.esm"),
    ])
    add("morrowind_missing_master", "Morrowind", [P("A.esp", ["Gone.esm"]), P("B.esp", ["A.esp"]), M("C.esm", ["AlsoGone.esm"])])

    # Games with other header layouts and flag rules.
    add("oblivion_masters", "Oblivion", [
        P("A.esp", ["Oblivion.esm", "B.esm"]), M("B.esm", ["Oblivion.esm"]), P("C.esp", ["A.esp"]),
        # No .esm rule on Oblivion: this one is not a master.
        P("NotMaster.esm"), M("Oblivion.esm"),
    ])
    add("morrowind_masters", "Morrowind", [
        P("A.esp", ["Morrowind.esm", "B.esm"]), M("B.esm", ["Morrowind.esm"]), P("C.esp", ["A.esp"]),
        P("NotMaster.esm"), M("Morrowind.esm"),
    ])
    add("fallout4_flags", "Fallout4", [
        P("A.esp"), P("Light.esl"), M("Master.esm"), P("Flagged.esp", flags=MASTER), P("B.esp", ["Master.esm"]),
    ])
    add("morrowind_metadata", "Morrowind", [P("A.esp"), P("B.esp"), M("M.esm")],
        masterlist="plugins:\n  - name: A.esp\n    after: [B.esp]\n")

    # Re-sorting the output changes nothing.
    add("idempotence", sse,
        [P(f"I{i}.esp", [f"I{(i * 7) % 13}.esp"] if (i * 7) % 13 < i else []) for i in (4, 12, 2, 9, 11, 1, 7, 3, 10, 5, 8, 6)],
        masterlist=groups_yaml([("late", ["default"])])
        + "plugins:\n  - name: I3.esp\n    group: late\n  - name: I9.esp\n    after: [I1.esp]\n",
        resort=True)
    add("idempotence_with_masters_and_early_loaders", sse, [
        P("Z.esp"), M("Dawnguard.esm"), P("Y.esp", ["Z.esp"]), M("Skyrim.esm"), M("Mod.esm", ["Dawnguard.esm"]), P("X.esp"),
    ], resort=True)

    # 150 plugins with masters, groups and some load-after entries.
    r = random.Random(150)
    names = [f"S{i:03d}.esp" for i in range(150)]
    plugins = [P(n, [names[j] for j in r.sample(range(i), min(i, r.randint(0, 2)))]) for i, n in enumerate(names)]
    r.shuffle(plugins)
    entries = {}
    for i in range(0, 150, 7):
        entries[i] = {"group": "late" if i % 2 else "early"}
    for i in range(3, 150, 11):
        j = (i * 5) % 150
        if j < i:
            entries.setdefault(i, {})["after"] = names[j]
    lines = ["plugins:"]
    for i, entry in sorted(entries.items()):
        lines.append(f"  - name: {names[i]}")
        if "group" in entry:
            lines.append(f"    group: {entry['group']}")
        if "after" in entry:
            lines.append(f"    after: [{entry['after']}]")
    add("stability_150_plugins", sse, plugins,
        masterlist=groups_yaml([("early", []), ("late", ["early"])]) + "\n".join(lines) + "\n", resort=True)

    return cases


# ---------------------------------------------------------------------- tier 2

EARLY_LOADERS = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"]


def random_case(seed):
    r = random.Random(seed)
    game_type = ["SkyrimSE", "SkyrimSE", "Starfield", "Oblivion"][seed % 4]
    count = r.randint(20, 200)
    # Every third seed is unconstrained and may produce an impossible load order; the rest point
    # backwards in a hidden order and are meant to be sortable.
    consistent = seed % 3 != 0

    names, kinds = [], []
    if game_type == "SkyrimSE" and r.random() < 0.6:
        for n in EARLY_LOADERS:
            if r.random() < 0.7:
                names.append(n)
                kinds.append("master")
    for i in range(count - len(names)):
        roll = r.random()
        kind = "master" if roll < 0.2 else "blueprint" if game_type == "Starfield" and roll < 0.27 else "plain"
        names.append(f"R{i:03d}{'.esm' if kind != 'plain' and r.random() < 0.8 else '.esp'}")
        kinds.append(kind)

    # Hidden order: masters, then plain, then blueprint masters, which is the order the partitions
    # concatenate in.
    rank = {"master": 0, "plain": 1, "blueprint": 2}
    hidden = sorted(range(len(names)), key=lambda i: (rank[kinds[i]], i))
    position = {i: p for p, i in enumerate(hidden)}

    def targets(i, limit, header=False):
        def allowed(j):
            if j == i:
                return False
            if not consistent:
                return True
            if position[j] >= position[i]:
                return False
            if header:
                # A plugin's masters: any plugin for a non-master, masters for a master.
                return kinds[i] == "plain" or kinds[j] in ("master", kinds[i])
            return kinds[j] == kinds[i]
        pool = [j for j in range(len(names)) if allowed(j)]
        return r.sample(pool, min(len(pool), r.randint(1, limit))) if pool else []

    plugins = []
    for i, name in enumerate(names):
        flags = MASTER if kinds[i] != "plain" else 0
        if kinds[i] == "blueprint":
            flags |= SF_BLUEPRINT
        masters = [names[j] for j in targets(i, 3, header=True)] if r.random() < 0.5 else []
        plugins.append(P(name, masters, flags))

    group_names = [f"g{i}" for i in range(r.randint(2, 6))]
    groups = []
    for gi, g in enumerate(group_names):
        groups.append((g, [h for h in group_names[:gi] + ["default"] if r.random() < 0.4]))
    if not consistent and r.random() < 0.5:
        groups.append(("default", r.sample(group_names, 1)))
    all_groups = group_names + ["default"]

    def entry_text(items):
        lines = ["plugins:"]
        for i, group, after, req in items:
            lines.append(f"  - name: {names[i]}")
            if group:
                lines.append(f"    group: {group}")
            if after:
                lines.append(f"    after: [{', '.join(names[j] for j in after)}]")
            if req:
                lines.append(f"    req: [{', '.join(names[j] for j in req)}]")
        return "\n".join(lines) + "\n"

    def entries(group_chance, edge_chance):
        out = []
        for i in range(len(names)):
            group = r.choice(all_groups) if r.random() < group_chance else None
            after = targets(i, 2) if r.random() < edge_chance else []
            req = targets(i, 2) if r.random() < edge_chance / 2 else []
            if group or after or req:
                out.append((i, group, after, req))
        return out

    masterlist = groups_yaml(groups) + entry_text(entries(0.5, 0.12))
    userlist = None
    if r.random() < 0.7:
        # A user group that extends a masterlist group can close a cycle, so only the unconstrained
        # seeds get after-groups from the user.
        user_groups = [(g, [h for h in all_groups if h != g and r.random() < 0.15] if not consistent else [])
                       for g in r.sample(group_names, 1)]
        userlist = groups_yaml(user_groups) + entry_text(entries(0.2, 0.06))

    order = list(range(len(names)))
    r.shuffle(order)
    return dict(
        name=f"seed_{seed:03d}", tier=2, seed=seed, game_type=game_type,
        plugins=[plugins[i] for i in order], masterlist=masterlist, userlist=userlist,
    )


# ---------------------------------------------------------------------- tier 3

# libloot treats these characters as the mark of a regex name.
_REGEX_CHARS = set(":\\*?|")
_PLUGIN_FILE = re.compile(r"^[^/\\:*?\"<>|]+\.(esm|esp|esl)$", re.IGNORECASE)


def literal_plugin_names(stripped_yaml):
    """Literal `- name:` entries of the top-level `plugins:` sequence.

    The stripped text is written by one emitter, so entries are lines of the form `- name: 'X'` at
    column 0 inside the `plugins:` block; there is no YAML parser in this interpreter.
    """
    names, seen, in_plugins = [], set(), False
    for line in stripped_yaml.split("\n"):
        if line.startswith("plugins:"):
            in_plugins = True
            continue
        if in_plugins and line and line[0] not in " -#":
            in_plugins = False
        if not in_plugins or not line.startswith("- name: "):
            continue
        name = line[len("- name: "):].strip()
        if name.startswith("'") and name.endswith("'"):
            name = name[1:-1].replace("''", "'")
        if (_REGEX_CHARS & set(name)) or not _PLUGIN_FILE.match(name) or name.lower() in seen:
            continue
        seen.add(name.lower())
        names.append(name)
    return names


def sha256_file(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


# Random masters drawn without regard to the masterlist's groups often contradict it, and libloot
# answers with a cycle. A seed starts with plenty of masters and falls back to fewer, and finally
# none, until the oracle can sort it; the cycle cases still in the corpus are real masterlist cycles.
TIER3_MASTER_CHANCES = [0.5, 0.25, 0.1, 0.0]


def real_masterlist_case(repo, seed, stripped_dir, hashes, master_chance):
    game_type = TIER3_GAMES[repo]
    masterlist_name = f"{repo}.masterlist.yaml"
    masterlist_path = os.path.join(stripped_dir, masterlist_name)
    prelude_path = os.path.join(stripped_dir, "prelude.yaml")
    # The recorded hashes must describe the files actually sorted with.
    for path, name in ((masterlist_path, masterlist_name), (prelude_path, "prelude.yaml")):
        if sha256_file(path) != hashes[name]["stripped"]:
            raise SystemExit(f"{path} does not match the stripped SHA-256 in sha256.json; rerun the export test")
    with open(masterlist_path, encoding="utf-8", newline="") as f:
        masterlist = f.read()
    with open(prelude_path, encoding="utf-8", newline="") as f:
        prelude = f.read()

    r = random.Random(f"{repo}/{seed}")
    literals = literal_plugin_names(masterlist)
    total = r.randint(100, 400)
    real = min(len(literals), round(total * 0.8))
    names = r.sample(literals, real)
    taken = {n.lower() for n in names}
    while len(names) < real + real // 4:
        name = f"Distractor{r.randrange(10000):04d}.{r.choice(['esp', 'esp', 'esm'])}"
        if name.lower() not in taken:
            taken.add(name.lower())
            names.append(name)

    # Masters are drawn from earlier plugins. A master file lists only master files, which is what
    # the game's own tools produce and keeps the masters partition from contradicting the masters edges.
    is_master = {n: n.lower().endswith((".esm", ".esl")) for n in names}
    base = sorted(names, key=lambda n: (not is_master[n], r.random()))
    plugins = []
    for i, name in enumerate(base):
        pool = [m for m in base[:i] if is_master[m] or not is_master[name]]
        masters = r.sample(pool, min(len(pool), r.randint(1, 3))) if pool and r.random() < master_chance else []
        plugins.append(P(name, masters, MASTER if is_master[name] else 0))
    r.shuffle(plugins)

    file = hashes[masterlist_name]
    return dict(
        name=f"{repo}_seed_{seed:03d}", tier=3, seed=seed, game_type=game_type, plugins=plugins,
        masterlist=masterlist, prelude=prelude, userlist=None,
        real_masterlist=dict(
            repository=repo,
            bundled_sha256=file["bundled"],
            stripped_sha256=file["stripped"],
            prelude_bundled_sha256=hashes["prelude.yaml"]["bundled"],
            prelude_stripped_sha256=hashes["prelude.yaml"]["stripped"],
        ),
    )


# ------------------------------------------------------------------------ main

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_out = os.path.join(here, "..", "..", "tests", "Games", "NexusMods.Games.Bethesda.Loot.Tests", "Corpus")
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--out", default=default_out, help="corpus directory")
    parser.add_argument("--tier", choices=["1", "2", "3"], action="append",
                        help="only these tiers (default: 1 and 2, plus 3 when --stripped-dir is given)")
    parser.add_argument("--stripped-dir",
                        help="directory written by the WriteStrippedMasterlistsForOracle test; enables tier 3")
    args = parser.parse_args()
    tiers = args.tier or (["1", "2", "3"] if args.stripped_dir else ["1", "2"])
    if "3" in tiers and not args.stripped_dir:
        parser.error("tier 3 needs --stripped-dir")

    cases = []
    if "1" in tiers:
        cases += tier1_cases()
    if "2" in tiers:
        cases += [random_case(seed) for seed in range(TIER2_SEEDS)]
    hashes = None
    if "3" in tiers:
        with open(os.path.join(args.stripped_dir, "sha256.json"), encoding="utf-8") as f:
            hashes = json.load(f)

    def results():
        for case in cases:
            yield (case, *run_case(case))
        if "3" in tiers:
            for repo in TIER3_GAMES:
                for seed in range(TIER3_SEEDS):
                    for chance in TIER3_MASTER_CHANCES:
                        case = real_masterlist_case(repo, seed, args.stripped_dir, hashes, chance)
                        expected, paths = run_case(case)
                        if "order" in expected:
                            break
                    yield case, expected, paths

    for case, expected, paths in results():
        fixture = {
            "libloot_version": loot.libloot_version(),
            "libloot_revision": loot.libloot_revision(),
            "generator_version": GENERATOR_VERSION,
            "seed": case.get("seed"),
            "game_type": case["game_type"],
            "format": GAMES[case["game_type"]]["format"],
            "header_version": GAMES[case["game_type"]]["version"],
            "name": case["name"],
            "plugins": case["plugins"],
            # A real masterlist is not stored; the hashes name the bundled file it came from.
            "masterlist": None if "real_masterlist" in case else case.get("masterlist"),
            "userlist": case.get("userlist"),
            "prelude": None if "real_masterlist" in case else case.get("prelude"),
            **({"real_masterlist": case["real_masterlist"]} if "real_masterlist" in case else {}),
            "expected": expected,
            "groups_paths": paths,
        }
        folder = os.path.join(args.out, f"tier{case['tier']}")
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, case["name"] + ".json"), "w", encoding="utf-8", newline="\n") as f:
            f.write(render(fixture) + "\n")
        outcome = "ok" if "order" in expected else "error " + expected["error"]["kind"]
        print(f"{case['name']}: {outcome}")


if __name__ == "__main__":
    sys.exit(main())
