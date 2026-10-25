#!/usr/bin/env bash
# Runs the offline test suite (everything except RequiresNetworking, RequiresApiKey,
# RequiresProton, RequiresSecretService and FlakeyTest) through Microsoft.Testing.Platform, inside the
# distrobox named by SILVER_BOX (default fedora) unless already in a container.
# Extra arguments go to `dotnet test`, e.g.
#   scripts/test.sh --project tests/SilverModManager.CLI.Tests
#
# The filters travel as the XunitArgs MSBuild property, which tests/Directory.Build.props
# maps onto TestingPlatformCommandLineArguments.
set -euo pipefail

# shellcheck source=lib/box.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/box.sh"
box_reexec "${BASH_SOURCE[0]}" "$@"

cd "$root"
run_low_priority
export_dotnet_env
export QT_QPA_PLATFORM=offscreen

# Test hosts build the real service graph, which touches the desktop session: it writes
# .desktop files and mimeapps entries under XDG_DATA_HOME/XDG_CONFIG_HOME and can reach
# the compositor. Point every XDG dir at a throwaway directory and drop the display
# variables so a test run can never change the user's file associations or open windows.
xdg_root="$(mktemp -d)"
trap 'rm -rf "$xdg_root"' EXIT
export XDG_DATA_HOME="$xdg_root/data" XDG_CONFIG_HOME="$xdg_root/config" \
       XDG_STATE_HOME="$xdg_root/state" XDG_CACHE_HOME="$xdg_root/cache"
unset DISPLAY WAYLAND_DISPLAY
# The distrobox shares the host's session bus, and Tmds.DBus.Protocol finds it only through this
# variable. A dead address means a secret store that slips past the in-memory backend gets
# Unavailable instead of the user's keyring. AvaloniaDBusTests starts its own dbus-daemon and
# connects to that address explicitly, so nothing in the suite needs the real bus.
export DBUS_SESSION_BUS_ADDRESS="unix:path=$xdg_root/no-bus"

# Exit code 8 is MTP's "zero tests ran". Helper projects and projects whose tests
# are all filtered out hit it, so it must not fail the run.
#
# dotnet test refuses more than one target, so the solution is only the default
# when the caller didn't name a project, directory or test modules.
target=(--solution SilverModManager.App.sln)
for arg in "$@"; do
    case "$arg" in
        --project|--project=*|--solution|--solution=*|--directory|--directory=*|--test-modules|--test-modules=*) target=() ;;
    esac
done

# Left alone, the whole suite starts one test host per project at once, on top of the build.
# SILVER_TEST_MODULES caps how many run together (default 8).
dotnet test "${target[@]}" --ignore-exit-code 8 \
    --max-parallel-test-modules "${SILVER_TEST_MODULES:-8}" \
    -p:XunitArgs="--filter-not-trait RequiresNetworking=True --filter-not-trait RequiresApiKey=True --filter-not-trait RequiresProton=True --filter-not-trait RequiresSecretService=True --filter-not-trait FlakeyTest=True" \
    "$@"
