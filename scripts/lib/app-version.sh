# shellcheck shell=bash
# Sourced by dev-package.sh; not run directly.
#
# Prints the version to stamp on a packaged build: SILVER_APP_VERSION when set, otherwise
# the nearest v<major>.<minor>.<patch> tag reachable from HEAD. A commit past that tag gets
# a -dev.<commits since the tag> suffix. The suffix only reaches the package name and the
# informational version: the assembly version, which the update checker compares, stays
# the tag's, so a dev build is offered the next release but not the one it grew from.
# With no tag it fails rather than invent a version.
#
# app_version [repo-dir]   (default: the current directory)
app_version() {
    if [[ -n "${SILVER_APP_VERSION:-}" ]]; then
        echo "$SILVER_APP_VERSION"
        return 0
    fi

    local repo="${1:-.}" described
    # --exclude drops pre-release tags such as v1.2.4-rc1, which the match glob also accepts.
    if ! described="$(git -C "$repo" describe --tags --long --match 'v[0-9]*.[0-9]*.[0-9]*' --exclude 'v*-*' 2>/dev/null)"; then
        echo "No v<major>.<minor>.<patch> tag is reachable from HEAD; tag a release or set SILVER_APP_VERSION." >&2
        return 1
    fi

    # --long always yields v<version>-<commits>-g<sha>, so the last two dashes are ours.
    local without_sha="${described%-g*}" tag commits
    commits="${without_sha##*-}"
    tag="${without_sha%-*}"
    tag="${tag#v}"
    if [[ "$commits" == 0 ]]; then
        echo "$tag"
    else
        echo "$tag-dev.$commits"
    fi
}
