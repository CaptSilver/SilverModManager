# SilverModManager

SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App) that downloads mods from [Nexus Mods](https://www.nexusmods.com).
It keeps upstream's mod library, loadouts and Nexus integration, and stores its data apart
from an installed copy of the upstream app.

It is not affiliated with or endorsed by Nexus Mods. It is a modified version of the Nexus Mods App, which is GPL-3.0-only;
see [`NOTICE`](NOTICE) for what that means here and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
for the fonts, icons and libraries it ships.

## Building

You need Linux and the .NET 10 SDK. [`CONTRIBUTING.md`](CONTRIBUTING.md) covers the
build, test and AppImage scripts and the rules for running them without touching your real home
directory. Developer notes inherited from upstream are in [`docs/developers`](docs/developers/index.md).

## License

GPL-3.0-only, see [`LICENSE.md`](LICENSE.md).
