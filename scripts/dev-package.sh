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

# Fails when the AppImage carries a usr/bin/runtimes/<name> folder other than the RID it was built
# for or a RID-neutral one (unix, any, linux). Bare OS names such as win or osx count as foreign, like
# the pattern in Directory.Build.targets. The image can't be run when it targets the other CPU, so
# list its squashfs, found by its "hsqs" magic.
check_runtimes() {
    local image="$1" want offset listing foreign found=0
    case "$2" in
        x86_64) want=linux-x64 ;;
        aarch64) want=linux-arm64 ;;
    esac
    # The runtime in front of the squashfs can contain the magic by chance, so take the first
    # offset unsquashfs accepts.
    for offset in $(grep -abo hsqs "$image" | cut -d: -f1); do
        if listing="$(unsquashfs -l -o "$offset" "$image" 2>/dev/null)"; then
            found=1
            break
        fi
    done
    (( found )) || { echo "$image has no readable squashfs section" >&2; exit 1; }
    foreign="$(sed -n 's|^.*/usr/bin/runtimes/\([^/]*\)/.*$|\1|p' <<<"$listing" | sort -u | grep -vxE -- "$want|unix|any|linux" || true)"
    if [[ -n "$foreign" ]]; then
        echo "$image ships runtimes for other platforms than $want:" >&2
        sed 's|^|  runtimes/|; s|$|/|' <<<"$foreign" >&2
        exit 1
    fi
}

# unsquashfs inspects the finished image, so fail before the build rather than after it.
command -v unsquashfs >/dev/null || { echo "unsquashfs (squashfs-tools) is needed to inspect the AppImage" >&2; exit 1; }

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
image="$(ls -1 Deploy/OUT/*."$arch".AppImage)"
check_runtimes "$image" "$arch"
echo "$image"
