#!/usr/bin/env bash
# Runs the offline test suite (everything except RequiresNetworking, RequiresApiKey
# and FlakeyTest) through Microsoft.Testing.Platform, inside the `fedora` distrobox
# unless already in a container. Extra arguments go to `dotnet test`, e.g.
#   scripts/test.sh --project tests/NexusMods.CLI.Tests
#
# The filters travel as the XunitArgs MSBuild property, which tests/Directory.Build.props
# maps onto TestingPlatformCommandLineArguments.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ -z "${CONTAINER_ID:-}" ]] && command -v distrobox >/dev/null; then
    # Quote each argument so ones with spaces survive the trip through bash -lc.
    # printf '%q ' with no arguments still prints '', hence the guard.
    args=""
    (( $# )) && args="$(printf '%q ' "$@")"
    exec distrobox enter fedora -- bash -lc "cd $(printf %q "$root") && scripts/test.sh $args"
fi

cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 QT_QPA_PLATFORM=offscreen

# Test hosts build the real service graph, which touches the desktop session: it writes
# .desktop files and mimeapps entries under XDG_DATA_HOME/XDG_CONFIG_HOME and can reach
# the compositor. Point every XDG dir at a throwaway directory and drop the display
# variables so a test run can never change the user's file associations or open windows.
xdg_root="$(mktemp -d)"
trap 'rm -rf "$xdg_root"' EXIT
export XDG_DATA_HOME="$xdg_root/data" XDG_CONFIG_HOME="$xdg_root/config" \
       XDG_STATE_HOME="$xdg_root/state" XDG_CACHE_HOME="$xdg_root/cache"
unset DISPLAY WAYLAND_DISPLAY

# Exit code 8 is MTP's "zero tests ran". Helper projects and projects whose tests
# are all filtered out hit it, so it must not fail the run.
#
# dotnet test refuses more than one target, so the solution is only the default
# when the caller didn't name a project, directory or test modules.
target=(--solution NexusMods.App.sln)
for arg in "$@"; do
    case "$arg" in
        --project|--project=*|--solution|--solution=*|--directory|--directory=*|--test-modules|--test-modules=*) target=() ;;
    esac
done

dotnet test "${target[@]}" --ignore-exit-code 8 \
    -p:XunitArgs="--filter-not-trait RequiresNetworking=True --filter-not-trait RequiresApiKey=True --filter-not-trait FlakeyTest=True" \
    "$@"
