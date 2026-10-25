#!/usr/bin/env bash
# Builds the solution inside the distrobox (SILVER_BOX, default fedora). Extra
# arguments go to `dotnet build`, e.g. scripts/dev-build.sh -c Release
set -euo pipefail

# shellcheck source=lib/box.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/box.sh"
box_reexec "${BASH_SOURCE[0]}" "$@"

cd "$root"
run_low_priority
export_dotnet_env
dotnet build SilverModManager.App.sln "$@"
