#!/usr/bin/env bash
# Builds an AppImage the way CI does (.github/workflows/build-linux-pupnet.yaml), inside
# the distrobox (SILVER_BOX, default fedora). The result lands in
# src/SilverModManager.App/Deploy/OUT, which is gitignored. The version comes from the
# nearest v<major>.<minor>.<patch> tag (see lib/app-version.sh); SILVER_APP_VERSION overrides
# it.
#
# Usage: dev-package.sh [--arch x86_64|aarch64] [pupnet arguments...]
# --arch defaults to x86_64 and, as the first argument, picks the target: aarch64 builds for
# linux-arm64 (PupNet's -r) and writes SilverModManager.aarch64.AppImage. Everything after it
# goes to pupnet.
set -euo pipefail

# shellcheck source=lib/box.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/box.sh"
# shellcheck source=lib/app-version.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/app-version.sh"
box_reexec "${BASH_SOURCE[0]}" "$@"

arch=x86_64
if [[ "${1:-}" == --arch ]]; then
    arch="${2:-}"
    shift $(( $# > 1 ? 2 : 1 ))
fi
pupnet_runtime=()
case "$arch" in
    x86_64) ;;
    aarch64) pupnet_runtime=(-r linux-arm64) ;;
    *)
        echo "--arch must be x86_64 or aarch64, not '$arch'" >&2
        exit 2
        ;;
esac

# Same pinned PupNet as CI.
pupnet_repo=https://github.com/kuiperzone/PupNet-Deploy.git
pupnet_commit=dabed0cc2063c5a2d2c4f780bb6718f4b90cfd16
pupnet_version=0.0.0-nma

export_dotnet_env
scratch_tmpdir
export PATH="$HOME/.dotnet/tools:$PATH"

# appimagetool is itself an AppImage. Unpack it instead of mounting it, so packaging
# needs neither fusermount nor /dev/fuse inside the box.
export APPIMAGE_EXTRACT_AND_RUN=1

if ! dotnet tool list --global | grep -qi "kuiperzone.pupnet.*$pupnet_version"; then
    # PupNet is built from a throwaway clone, not in-tree: this repo's central package
    # management breaks its build.
    src="$(mktemp -d)"
    trap 'rm -rf "$src" "$scratch_tmp"' EXIT
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
pupnet -y -v "$version" -k AppImage -p DefineConstants=INSTALLATION_METHOD_APPIMAGE "${pupnet_runtime[@]}" "$@"
ls -1 Deploy/OUT/*."$arch".AppImage
