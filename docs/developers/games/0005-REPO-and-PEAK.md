## General Info

- Name: R.E.P.O. and PEAK
- Release Date: 2025
- Engine: Unity, modded through BepInEx

Both games share one implementation, `SilverModManager.Games.Unity`. A game there is a small class that supplies a
`UnityGameDefinition`; installers, diagnostics and the loader logic are common.

### Stores and Ids

| | R.E.P.O. | PEAK |
|---|---|---|
| Steam app id | `3241660` | `3527290` |
| Executable | `REPO.exe` | `PEAK.exe` |
| Data folder | `REPO_Data` | `PEAK_Data` |
| Game id | `Unity.Repo` | `Unity.Peak` |
| Thunderstore community | `repo` | `peak` |

Both are Windows-only on Steam and run through Proton on Linux. Mods come from Thunderstore, so neither game has a
Nexus Mods game id in the app.

### Useful Links

- [Thunderstore packaging docs](https://wiki.thunderstore.io/mods/creating-a-package)
- [BepInEx](https://docs.bepinex.dev)

## Overview of Mod loading process(es)

BepInEx loads plugins into the game through a doorstop proxy, `winhttp.dll`, placed next to the executable. The game
loads the proxy instead of the system `winhttp.dll`, and the proxy starts BepInEx before Unity does.

Under Proton that only works if Wine is told to prefer the native DLL over its own. The launch needs
`WINEDLLOVERRIDES="winhttp=n,b"`. When the app launches the game it sets this itself. When the player starts the
game from Steam, the override has to be in the game's launch options. The diagnostic for it checks the prefix and
the Steam launch line, not what the app would set, so it keeps showing until one of them carries the override. The requirement only applies while the loader
pack is installed; the override would otherwise hide Wine's own DLL for no reason.

## Uploaded Files Structure

Everything comes from Thunderstore as a zip with `manifest.json`, `README.md` and `icon.png` at its root (and
sometimes `CHANGELOG.md`).

### Loader pack

The pack has one root folder (`BepInExPack/` for R.E.P.O., `BepInExPack_PEAK/` for PEAK) holding `winhttp.dll`,
`doorstop_config.ini` and `BepInEx/core/`. The installer drops the root folder and writes the rest into the game
root, so `winhttp.dll` ends up beside the executable. The root's manifest, readme, icon and changelog are skipped.

An archive counts as the pack when its files show the three markers above, or when its name is the game's loader
package or an alias. The name check covers BepInEx 6 packs, which have no `BepInEx.Preloader.dll`.

### Plugins

The routing follows r2modman, so a package lands where its author tested it:

- A folder called `plugins`, `patchers`, `core` or `monomod`, at any depth, goes to
  `BepInEx/<folder>/<Author-Name>/` with its subfolders kept.
- `config` is matched the same way but has no package folder, since BepInEx reads
  `BepInEx/config/<guid>.cfg` directly.
- A file outside those folders is flattened to its file name, as r2modman does. `.mm.dll` goes to `monomod`,
  anything else to `plugins`.
- Matching ignores case. Two files that resolve to the same target fail the install instead of overwriting each
  other.

`<Author-Name>` is the archive's file name with the version cut off: Thunderstore downloads are named
`Author-Name-1.2.3.zip`. Only when the name has no version is the manifest read, giving `namespace-name`, or just
`name` since `namespace` is optional. Without a usable manifest the file name is used as it is.

## Additional Considerations for Manager

### Loader package per game

| | Loader package | Aliases |
|---|---|---|
| R.E.P.O. | `BepInEx-BepInExPack` | `bbepis-BepInExPack`, `xiaoxiao921-BepInExPack` |
| PEAK | `BepInEx-BepInExPack_PEAK` | `BepInEx-BepInExPack`, `bbepis-BepInExPack`, `xiaoxiao921-BepInExPack` |

Mods declare whichever of these they were built against; many PEAK mods depend on the plain `BepInEx-BepInExPack`.
The resolver treats a dependency on any alias as a dependency on the game's own loader package, so one pack is
installed and not one per spelling.

### Reading a dependency string

A Thunderstore dependency is `Namespace-Name-Version`. Team names can contain hyphens, package names cannot
(`a-z A-Z 0-9 _` only), so:

1. The version is whatever follows the last dash.
2. What is left is `Namespace-Name`. With one dash the split is obvious. With more, the split is ambiguous
   (`Some-Team-Mod` could be `Some-Team` + `Mod` or `Some` + `Team-Mod`), so each candidate is looked up on
   Thunderstore and the one that exists wins.

The same last-dash rule is used on archive file names when a manifest is missing.

## Essential Mods & Tools

- The loader pack for the game, see above. Without it a plugin installs but nothing loads it, and the app reports
  a missing-loader diagnostic against the first plugin.

## Deployment Strategy

Plain file deployment into the game folder. Nothing is generated at deploy time; the loader pack and plugins are
ordinary loadout files. The synchronizer ignores what BepInEx writes while the game runs, `BepInEx/LogOutput.log`
and `BepInEx/cache/`, so a play session does not show up as loadout changes.

## Work To Do

- Another BepInEx Unity game needs a class like `Repo` or `Peak` that supplies its `UnityGameDefinition`, plus its
  registration in `SilverModManager.Games.Unity.Services`.

## Misc Notes
