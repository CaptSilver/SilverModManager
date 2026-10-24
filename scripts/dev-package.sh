#!/usr/bin/env bash
# Builds an AppImage the way CI does (.github/workflows/build-linux-pupnet.yaml), inside
# the distrobox (SILVER_BOX, default fedora). The result lands in
# src/SilverModManager.App/Deploy/OUT, which is gitignored. The version comes from the
# nearest v<major>.<minor>.<patch> tag (see lib/app-version.sh); SILVER_APP_VERSION overrides
# it. Extra arguments go to pupnet.
set -euo pipefail

# shellcheck source=lib/box.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/box.sh"
# shellcheck source=lib/app-version.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/app-version.sh"
box_reexec "${BASH_SOURCE[0]}" "$@"

# Same pinned PupNet as CI.
pupnet_repo=https://github.com/kuiperzone/PupNet-Deploy.git
pupnet_commit=dabed0cc2063c5a2d2c4f780bb6718f4b90cfd16
pupnet_version=0.0.0-nma

export_dotnet_env
export PATH="$HOME/.dotnet/tools:$PATH"

# appimagetool is itself an AppImage. Unpack it instead of mounting it, so packaging
# needs neither fusermount nor /dev/fuse inside the box.
export APPIMAGE_EXTRACT_AND_RUN=1

if ! dotnet tool list --global | grep -qi "kuiperzone.pupnet.*$pupnet_version"; then
    # PupNet is built from a throwaway clone, not in-tree: this repo's central package
    # management breaks its build.
    src="$(mktemp -d)"
    trap 'rm -rf "$src"' EXIT
    git clone "$pupnet_repo" "$src"
    git -C "$src" checkout "$pupnet_commit"
    (cd "$src" && dotnet pack -c Release -o . -p:Version="$pupnet_version")
    dotnet tool uninstall --global KuiperZone.PupNet >/dev/null 2>&1 || true
    dotnet tool install --global --add-source "$src" KuiperZone.PupNet --version "$pupnet_version"
fi

# PupNet targets .NET 8, which the box does not carry; let it run on the installed
# runtime rather than installing an extra one.
export DOTNET_ROLL_FORWARD=Major

version="$(app_version "$root")"
cd "$root/src/SilverModManager.App"
pupnet -y -v "$version" -k AppImage -p DefineConstants=INSTALLATION_METHOD_APPIMAGE "$@"
ls -1 Deploy/OUT/*.AppImage
