#!/usr/bin/env bash
# One-time (and safe to repeat) developer setup. Installs the .NET SDK and its runtime
# libraries inside the distrobox (SILVER_BOX, default fedora), fetches the submodules
# and restores NuGet packages. Nothing is installed on the host. The box must already
# exist; see scripts/lib/box.sh.
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

cd "$root"
git submodule update --init --recursive

export_dotnet_env
dotnet restore NexusMods.App.sln
