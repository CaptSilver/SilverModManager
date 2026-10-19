#!/usr/bin/env bash
# Runs the offline test suite (everything except RequiresNetworking, RequiresApiKey
# and FlakeyTest) through Microsoft.Testing.Platform, inside the `fedora` distrobox
# unless already in a container. Extra arguments go to `dotnet test`, e.g.
#   scripts/test.sh --project tests/NexusMods.CLI.Tests
#
# The xunit v3 and TUnit runners reject each other's filter options, so the filters
# travel as MSBuild properties (XunitArgs / TUnitArgs) that each test project maps
# onto TestingPlatformCommandLineArguments.
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

exec dotnet test "${target[@]}" --ignore-exit-code 8 \
    -p:XunitArgs="--filter-not-trait RequiresNetworking=True --filter-not-trait RequiresApiKey=True --filter-not-trait FlakeyTest=True" \
    -p:TUnitArgs="" \
    "$@"
