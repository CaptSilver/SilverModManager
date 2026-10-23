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

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py            # tiers 1 and 2
    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py --tier 1   # hand-written cases only

Tier 3 needs the stripped masterlists, which only the C# side can produce (this interpreter has no
YAML library). Write them, then point the generator at them:

    export LOOT_ORACLE_STRIP_DIR=$(mktemp -d)
    scripts/test.sh --project tests/Games/NexusMods.Games.Bethesda.Loot.Tests    # writes the files; the export test is skipped without the variable
    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate.py --tier 3 --stripped-dir "$LOOT_ORACLE_STRIP_DIR"

The stripped YAML stays in that directory and is never committed. Regenerate tier 3 after refreshing
the bundled masterlists: `CorpusTests` hashes the bundled files and fails until the corpus is rebuilt.

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
- `tier3/`: the real bundled masterlists and prelude with every `condition` and `constraint` removed
  (`MasterlistStripper`), `<repo>_seed_000` to `_003` for each of the ten repositories. Each case has
  synthetic plugins named after the masterlist's literal plugin entries (80% of up to 400, so the
  smaller masterlists give smaller cases) plus 20% distractors, random masters drawn from earlier
  plugins and a random current order. The case holds the SHA-256 of the bundled file and of its
  stripped form, for the masterlist and the prelude, instead of the text. `CorpusTests` strips the
  bundled resource itself, checks all four hashes, then sorts. Masters picked at random often
  contradict the real group order, so a seed that ends in a cycle is retried with fewer masters (down
  to none) and the first sortable version is kept. Enderal has no binding game type and runs as Skyrim SE.

To add a hand-written case, add an `add(...)` call in `tier1_cases()`, regenerate, and look at the
JSON before trusting it: the oracle is right by definition, so a case that does not exercise what its
name says passes silently. A tier-2 failure is a bug in the port; look into it before touching the case.

## Real load orders

`generate_real.py` writes `Corpus/real/*.json`, cases built from real installs instead of seeds.
`RealCorpusTests` sorts them, logs the C# time next to the one libloot's `sort_plugins` took when the case
was generated, and on a mismatch names the first index that differs and the edges on both plugins
(`PluginSorter.Sort(..., recordEdges: true)` fills `SortResult.Edges`).

- `tempus_maledictum`: the `loadorder.txt` of the Tempus Maledictum 8.0.6 Wabbajack list (Skyrim SE, 2174
  plugins). Only plugin names are kept; the case records the list's machine name, version and the hashes
  of the `.wabbajack` file. Flags come from the extension, and an `.esp` listed among the masters gets the
  master flag, because a name cannot say that USSEP is one. Every plugin masters the five base game
  plugins and the Creation Club `.esm` files; no plugin has records.

      "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate_real.py wabbajack --allow-network \
          --machine-url tempus_maledictum --profile "Tempus Maledictum - CS Profile" \
          --download-dir <scratch> --stripped-dir "$LOOT_ORACLE_STRIP_DIR" --name tempus_maledictum

  This is the only step that uses the network (about 1 GB, kept in `--download-dir`, never in the repo)
  and it refuses to run without `--allow-network`.
- `oblivion_remastered_instance`: the headers of the plugins a Mod Organizer instance of Oblivion Remastered
  enables, read with the app's own reader. The files are only read. The C# test exports names, flags and
  masters, then the generator sorts them with the bundled `oblivion-remastered` masterlist:

      LOOT_ORACLE_OBLIVION_INSTANCE=<instance dir> LOOT_ORACLE_OBLIVION_GAME_DATA=<game's Data dir> \
      LOOT_ORACLE_HEADERS_OUT=<scratch>/headers.json scripts/test.sh --project tests/Games/NexusMods.Games.Bethesda.Loot.Tests
      "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/generate_real.py headers --headers <scratch>/headers.json \
          --repository oblivion-remastered --stripped-dir "$LOOT_ORACLE_STRIP_DIR" --name oblivion_remastered_instance

Like tier 3, these cases name the bundled masterlist by hash, so refreshing the masterlists means regenerating them.

## Comparing with a real load order

`sort_real.py` is a user check, not a test. Give it a repository name and a text file with one plugin
per line in the current order (`Name.esp :: Master1.esm, Master2.esp`) and it prints libloot's order
using the real masterlist with its conditions:

    "$LOOT_ORACLE_PYTHON" -I tools/loot-oracle/sort_real.py skyrimse my-load-order.txt

Use it to compare the app's Sort with LOOT against libloot on your own plugins. The plugins are
header-only stubs, so conditions that read versions or checksums cannot match what the real files would.

## Load time

`MasterlistLoadTimingTests` parses the Skyrim SE masterlist with the prelude three times and asserts
the median is under two seconds. It is about 250 ms on the development machine. Past ~400 ms the
parsed document should be cached per masterlist revision in the store.

## What is not covered

libloot logs, and does not report, a plugin pair with no edge between them after tie-breaking, so the
oracle cannot show it. The tie-break is meant to rule that out, and `CorpusTests` asserts the port
reports none. Conditions are not evaluated by the port, and the corpus metadata has none (tier 3 strips the real ones).
