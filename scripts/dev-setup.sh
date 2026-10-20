#!/usr/bin/env bash
# One-time (and safe to repeat) developer setup. Installs the .NET SDK and its runtime
# libraries inside the distrobox (SILVER_BOX, default fedora), makes the host's /mnt Steam
# libraries reachable there, fetches the submodules and restores NuGet packages. Nothing
# is installed on the host. The box must already exist; see scripts/lib/box.sh.
set -euo pipefail

# shellcheck source=lib/box.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/box.sh"
box_reexec "${BASH_SOURCE[0]}" "$@"

# box_reexec falls through when distrobox is missing. Refuse rather than run sudo dnf
# against the host, which on an ostree system is read-only anyway.
[[ -n "${CONTAINER_ID:-}" || -e /run/.containerenv ]] || {
    echo "dev-setup.sh only installs packages inside a container; install distrobox or run it in one." >&2
    exit 1
}

command -v dnf >/dev/null || {
    echo "dev-setup.sh installs packages with dnf; on another distro install the .NET 10 SDK yourself." >&2
    exit 1
}

# dotnet-sdk-10.0 pulls in the runtime. libicu is a hard requirement of .NET
# globalization, listed because the SDK package does not always drag it in.
sudo dnf install -y dotnet-sdk-10.0 libicu git

# On Bazzite and other ostree hosts /mnt is a symlink to var/mnt, so Steam records extra
# libraries as /mnt/<name>. distrobox mounts the host's /var/mnt, but the box's own /mnt
# is an empty directory and those paths don't resolve, which hides every game on them.
# Link each /var/mnt entry into /mnt; anything already there is left alone.
host_mnt="$(readlink /run/host/mnt 2>/dev/null || true)"
if [[ "$host_mnt" == var/mnt || "$host_mnt" == /var/mnt ]] && [[ ! -L /mnt ]]; then
    shopt -s nullglob
    for dir in /var/mnt/*/; do
        name="$(basename "$dir")"
        [[ -e "/mnt/$name" || -L "/mnt/$name" ]] || sudo ln -s "/var/mnt/$name" "/mnt/$name"
    done
    shopt -u nullglob
fi

cd "$root"
git submodule update --init --recursive

export_dotnet_env
dotnet restore NexusMods.App.sln
