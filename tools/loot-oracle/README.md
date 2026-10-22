# LOOT oracle

The sort in `src/NexusMods.Games.Bethesda.Loot` is a port of libloot, and the port is checked
against libloot itself. `generate.py` runs libloot's Python binding over synthetic plugins and
metadata and writes the answers to `tests/Games/NexusMods.Games.Bethesda.Loot.Tests/Corpus/`.
`CorpusTests` reads those JSON files and needs no Python. Only regenerating the corpus does.

## The interpreter

`LOOT_ORACLE_PYTHON` names a Python whose site-packages hold the `loot` module (libloot 0.29.6,
revision `136f398`). On this machine that is the interpreter bundled with Fluorine, used read-only:

    export LOOT_ORACLE_PYTHON=/home/bazzite/build/Fluorine-Manager/build/staging/python/bin/python3.12
    "$LOOT_ORACLE_PYTHON" -I -c "import loot; print(loot.libloot_version(), loot.libloot_revision())"
    # 0.29.6 136f398

Nothing from that tree is copied into this repo. If it is gone, `build-oracle.sh <scratch dir>`
clones libloot at tag 0.29.6, builds the binding with maturin in the `fedora` distrobox and prints the
`export` line to use instead.

## Regenerating

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py            # both tiers
    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py --tier 1   # hand-written cases only

`--out <dir>` writes somewhere else. The run is deterministic: the same libloot and generator give
the same files, so a diff after regenerating is either a changed case or a changed oracle. Each file
records `libloot_version`, `libloot_revision`, `generator_version` and the case's `seed`, and
`CorpusTests` refuses a corpus from a different libloot version.

The script uses only the standard library and `loot`; that interpreter has nothing else
(`larian_formats`, `loot`, `psutil`, `vdf`). Plugins are written with the byte layout of
`PluginBytes` in the LoadOrder tests, into a temp `Data/` (`Data Files/` for Morrowind). Headers
are loaded with `load_plugin_headers`, not `load_plugins`, because Morrowind, OpenMW and Starfield
refuse to fully load a plugin whose masters are missing, and a record-less plugin has nothing but a
header anyway. That also means no overlap edges, so the port's skipped overlap input changes nothing.

## Tiers

- `tier1/`: hand-written, one case per edge type and per quirk (cycles with libloot's exact text,
  group precedence, tied group paths, tie-break path choice, regex names, merge keys, prelude anchors,
  Starfield blueprint masters, missing masters, Oblivion and Morrowind layouts, idempotence, 150 plugins).
- `tier2/`: seeded random cases, `seed_000` to `seed_039`: 20 to 200 plugins, a random masters DAG,
  a random groups DAG, random `after`/`req` entries and a random current order. Seeds divisible by 3 are
  unconstrained and end in a cycle error; the rest are built to sort.

To add a hand-written case, add an `add(...)` call in `tier1_cases()`, regenerate, and look at the
JSON before trusting it: the oracle is right by definition, so a case that does not exercise what its
name says passes silently. A tier-2 failure is a bug in the port; look into it before touching the case.

## What is not covered

libloot logs, and does not report, a plugin pair with no edge between them after tie-breaking, so the
oracle cannot show it. The tie-break is meant to rule that out, and `CorpusTests` asserts the port
reports none. Conditions are not evaluated by the port, and the corpus metadata has none.
