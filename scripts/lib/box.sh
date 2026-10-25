# shellcheck shell=bash
# shellcheck disable=SC2034 # root and box are read by the sourcing scripts
# Sourced by the scripts in this directory; not run directly.
#
# Builds, tests and packaging happen inside a distrobox so the host OS never needs a
# .NET SDK or compilers. SILVER_BOX names the box (default: fedora).

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
box="${SILVER_BOX:-fedora}"

# Re-runs the calling script inside the box, unless we are already in a container or
# distrobox is not installed (CI runs the scripts directly). Call it first, passing
# the script's own path and arguments: box_reexec "${BASH_SOURCE[0]}" "$@".
box_reexec() {
    # basename: the caller may have been started with a relative path.
    local script
    script="$root/scripts/$(basename "$1")"
    shift
    [[ -z "${CONTAINER_ID:-}" ]] && command -v distrobox >/dev/null || return 0

    # `distrobox enter` offers to create a missing box, which would hang or build a
    # container nobody asked for. Fail with the fix instead.
    if ! distrobox list --no-color 2>/dev/null | awk -F'|' -v b="$box" '{gsub(/ /, "", $2)} $2 == b {f = 1} END {exit !f}'; then
        echo "distrobox '$box' does not exist. Create it (distrobox create --name $box --image fedora-toolbox:latest)" >&2
        echo "or point SILVER_BOX at an existing box." >&2
        exit 1
    fi

    # Quote each argument so ones with spaces survive the trip through bash -lc.
    # printf '%q ' with no arguments still prints '', hence the guard.
    local args=""
    (( $# )) && args="$(printf '%q ' "$@")"
    exec distrobox enter "$box" -- bash -lc "cd $(printf %q "$root") && $(printf %q "$script") $args"
}

# Builds and test runs share the machine with someone using it. Lowest IO priority and a high
# nice level keep the desktop and games responsive; the run just takes longer while something
# else is busy. Children inherit both, so dotnet, MSBuild and every test host run the same way.
# SILVER_NICE sets the nice level (default 15); 0 leaves priority alone.
run_low_priority() {
    local level="${SILVER_NICE:-15}"
    (( level > 0 )) || return 0
    # A machine that refuses either still builds, just at normal priority, so warn and go on.
    renice -n "$level" -p $$ >/dev/null || echo "warning: could not lower the CPU priority of this run" >&2
    if command -v ionice >/dev/null; then
        ionice -c 2 -n 7 -p $$ || echo "warning: could not lower the IO priority of this run" >&2
    fi
}

# Nothing MSBuild starts should outlive the run. Directory.Build.rsp already turns off
# node reuse; the variable keeps it off under -noAutoResponse, and the MSBuild server is
# a separate resident process the response file doesn't cover.
export_dotnet_env() {
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
}
