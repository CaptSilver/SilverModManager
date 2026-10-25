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
scratch_root=""
reflink_root=""
trap 'rm -rf "$xdg_root" "$scratch_root" "$reflink_root"' EXIT
export XDG_DATA_HOME="$xdg_root/data" XDG_CONFIG_HOME="$xdg_root/config" \
       XDG_STATE_HOME="$xdg_root/state" XDG_CACHE_HOME="$xdg_root/cache"
unset DISPLAY WAYLAND_DISPLAY
# The distrobox shares the host's session bus, and Tmds.DBus.Protocol finds it only through this
# variable. A dead address means a secret store that slips past the in-memory backend gets
# Unavailable instead of the user's keyring. AvaloniaDBusTests starts its own dbus-daemon and
# connects to that address explicitly, so nothing in the suite needs the real bus.
export DBUS_SESSION_BUS_ADDRESS="unix:path=$xdg_root/no-bus"

# Test data goes to tmpfs instead of the test binaries' folder, which sits on the encrypted checkout disk
# (every write queues behind dmcrypt). TestScratch.Root reads SILVER_TEST_SCRATCH. /dev/shm is mounted noexec
# in the distrobox, so it only holds data; tests that run scripts they write keep using the temp directory,
# which is tmpfs on this machine. TMPDIR moves to /dev/shm only when the temp directory is a real disk and
# /dev/shm can run programs. Tests that need a second device read the original temp directory from
# SILVER_TEST_HOST_TMPDIR.
export SILVER_TEST_HOST_TMPDIR="${TMPDIR:-/tmp}"
if [[ -d /dev/shm && "$(stat -f -c %T /dev/shm)" == tmpfs ]]; then
    scratch_root="$(mktemp -d /dev/shm/silvermod-tests.XXXXXX)"
    export SILVER_TEST_SCRATCH="$scratch_root"
    if [[ "$(stat -f -c %T "$SILVER_TEST_HOST_TMPDIR")" != tmpfs ]]; then
        printf '#!/bin/sh\n' > "$scratch_root/exec-probe"
        chmod +x "$scratch_root/exec-probe"
        if "$scratch_root/exec-probe" 2>/dev/null; then
            export TMPDIR="$scratch_root"
        else
            echo "warning: $SILVER_TEST_HOST_TMPDIR is not tmpfs and /dev/shm is noexec; temp files stay on disk" >&2
        fi
        rm -f "$scratch_root/exec-probe"
    fi
else
    echo "warning: /dev/shm is not tmpfs; test data stays beside the test binaries" >&2
fi

# tmpfs can't reflink, so the tests that deploy with the reflink backend get a scratch folder on the host's
# OS disk (unencrypted btrfs). Inside the distrobox /var/tmp is the container's own, backed by the home disk,
# so the host's is reached through /run/host.
reflink_parent=/var/tmp
[[ -d /run/host/var/tmp ]] && reflink_parent=/run/host/var/tmp
if reflink_root="$(mktemp -d "$reflink_parent/silvermod-reflink.XXXXXX" 2>/dev/null)" \
    && [[ "$(stat -f -c %T "$reflink_root")" == btrfs ]] \
    && echo x > "$reflink_root/probe" && cp --reflink=always "$reflink_root/probe" "$reflink_root/probe.clone" 2>/dev/null; then
    rm -f "$reflink_root/probe" "$reflink_root/probe.clone"
    export SILVER_TEST_SCRATCH_REFLINK="$reflink_root"
else
    [[ -n "$reflink_root" ]] && rm -rf "$reflink_root"
    reflink_root=""
    echo "warning: no reflink-capable folder under $reflink_parent; reflink tests use the test binaries' folder" >&2
fi

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
