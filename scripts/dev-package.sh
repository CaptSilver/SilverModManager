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
# Prints the byte offset of the squashfs the AppImage's runtime is followed by. The runtime can
# contain the "hsqs" magic by chance, so this takes the first offset unsquashfs accepts.
squashfs_offset() {
    local image="$1" offset
    for offset in $(grep -abo hsqs "$image" | cut -d: -f1); do
        if unsquashfs -l -o "$offset" "$image" >/dev/null 2>&1; then
            echo "$offset"
            return
        fi
    done
    echo "$image has no readable squashfs section" >&2
    exit 1
}

check_runtimes() {
    local image="$1" want offset listing foreign
    case "$2" in
        x86_64) want=linux-x64 ;;
        aarch64) want=linux-arm64 ;;
    esac
    offset="$(squashfs_offset "$image")"
    listing="$(unsquashfs -l -o "$offset" "$image")"
    foreign="$(sed -n 's|^.*/usr/bin/runtimes/\([^/]*\)/.*$|\1|p' <<<"$listing" | sort -u | grep -vxE -- "$want|unix|any|linux" || true)"
    if [[ -n "$foreign" ]]; then
        echo "$image ships runtimes for other platforms than $want:" >&2
        sed 's|^|  runtimes/|; s|$|/|' <<<"$foreign" >&2
        exit 1
    fi
}

# The runtime PupNet's appimagetool puts in front of the image dlopens libfuse.so.2, which SteamOS
# (the Steam Frame) and other current systems no longer ship, so the image would not start there.
# AppImage's static type2 runtime links FUSE in and needs only the fusermount3 helper. It is pinned
# to a dated release and checked against that release's published digest.
runtime_release=20251108
declare -A runtime_sha256=(
    [x86_64]=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
    [aarch64]=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444
)

# Puts the static runtime in front of the image's squashfs, in place of the one it has.
swap_runtime() {
    local image="$1" arch="$2" cache runtime offset
    cache="${XDG_CACHE_HOME:-$HOME/.cache}/silvermodmanager-build/type2-runtime-$runtime_release"
    runtime="$cache/runtime-$arch"
    if [[ ! -f "$runtime" ]] || ! sha256sum -c <<<"${runtime_sha256[$arch]}  $runtime" >/dev/null 2>&1; then
        mkdir -p "$cache"
        curl -fsSL -o "$runtime.part" "https://github.com/AppImage/type2-runtime/releases/download/$runtime_release/runtime-$arch"
        sha256sum -c <<<"${runtime_sha256[$arch]}  $runtime.part" >/dev/null || { echo "runtime-$arch does not match its pinned digest" >&2; rm -f "$runtime.part"; exit 1; }
        mv "$runtime.part" "$runtime"
    fi
    offset="$(squashfs_offset "$image")"
    { cat "$runtime"; tail -c +"$((offset + 1))" "$image"; } > "$image.new"
    chmod +x "$image.new"
    mv "$image.new" "$image"
    if grep -q 'libfuse\.so\.2' <(head -c "$(stat -c %s "$runtime")" "$image"); then
        echo "$image still carries a runtime that loads libfuse.so.2" >&2
        exit 1
    fi
}

# A bundled library that calls FreeType must name a library that brings it in (libfreetype or
# libfontconfig). Desktop systems usually have FreeType loaded already, which hides the gap, but on
# SteamOS the first call crashes the app at startup. SkiaSharp 3.119.0-3.119.2 shipped such an
# arm64 build.
check_freetype_links() {
    local image="$1" offset dir lib undefined needed bad=""
    offset="$(squashfs_offset "$image")"
    dir="$(mktemp -d)"
    unsquashfs -q -no-xattrs -o "$offset" -d "$dir/root" "$image" 'usr/bin/*.so*' >/dev/null
    for lib in "$dir"/root/usr/bin/*.so*; do
        readelf -h "$lib" >/dev/null 2>&1 || continue
        undefined="$(readelf --dyn-syms -W "$lib" | awk '$7 == "UND" { print $8 }')"
        grep -q '^FT_' <<<"$undefined" || continue
        needed="$(readelf -d "$lib")"
        grep -qE 'NEEDED.*\[lib(freetype|fontconfig)\.so' <<<"$needed" || bad+="  ${lib##*/}"$'\n'
    done
    rm -rf "$dir"
    if [[ -n "$bad" ]]; then
        echo "$image bundles libraries that call FreeType without loading it:" >&2
        printf '%s' "$bad" >&2
        exit 1
    fi
}

# unsquashfs inspects the finished image, so fail before the build rather than after it.
command -v unsquashfs >/dev/null || { echo "unsquashfs (squashfs-tools) is needed to inspect the AppImage" >&2; exit 1; }
command -v readelf >/dev/null || { echo "readelf (binutils) is needed to inspect the AppImage" >&2; exit 1; }

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
swap_runtime "$image" "$arch"
check_runtimes "$image" "$arch"
check_freetype_links "$image"
echo "$image"
