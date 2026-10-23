# Contributing

SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App),
licensed under the GPL v3 (see `LICENSE.md`). Code that came from upstream keeps its original
copyright and license; new contributions are accepted under the same GPL v3 terms.

## Prerequisites

You need Linux and the .NET 10 SDK (`global.json` pins 10.0.100 and allows newer feature bands).
The scripts expect to run in a [distrobox](https://distrobox.it/) named `fedora`, so nothing gets
installed on your host. Create the box once:

```bash
distrobox create --name fedora --image fedora-toolbox:latest
```

Use another box by setting `SILVER_BOX=<name>`. Inside a container, or on a machine without
distrobox, the scripts run in place. `dev-setup.sh` refuses to run outside a container, so
elsewhere install the .NET 10 SDK yourself, run `git submodule update --init --recursive` and
`dotnet restore NexusMods.App.sln`, and use the other scripts as they are.

## Scripts

All of these re-run themselves inside the box, so call them from anywhere on the host.

| Command | What it does |
|---|---|
| `scripts/dev-setup.sh` | `dnf install`s `dotnet-sdk-10.0`, `libicu` and `git` in the box, fetches the submodules and runs `dotnet restore`. Safe to repeat. |
| `scripts/dev-build.sh` | `dotnet build NexusMods.App.sln`. Extra arguments go to `dotnet build`, e.g. `scripts/dev-build.sh -c Release`. |
| `scripts/dev-package.sh` | Builds an AppImage with PupNet. The result lands in `src/NexusMods.App/Deploy/OUT`. `SILVER_APP_VERSION` sets the version (default `0.0.1`). |
| `scripts/test.sh` | Runs the offline test suite. Extra arguments go to `dotnet test`. |

First time through: `scripts/dev-setup.sh`, then `scripts/dev-build.sh`, then `scripts/test.sh`.

## Running tests

```bash
scripts/test.sh                                          # whole suite
scripts/test.sh --project tests/NexusMods.CLI.Tests      # one test project
```

`test.sh` leaves out tests tagged with these xUnit traits:

- `RequiresNetworking`: talks to a real server. CI runs these in a separate job.
- `RequiresApiKey`: needs a Nexus Mods API key in the `NEXUS_API_KEY` environment variable.
  Put it on the test with `[Trait("RequiresApiKey", "True")]`, plus `RequiresNetworking` if it
  also goes online.
- `RequiresSecretService`: needs a Secret Service on a private session bus; the fixture starts one
  with `dbus-run-session` inside the box.
- `FlakeyTest`: fails intermittently and is excluded until someone fixes it. Don't add this to
  hide a test you broke.

A test that needs none of those should have no trait. To run the tagged ones, call `dotnet test`
yourself with `-p:XunitArgs="--filter-trait RequiresNetworking=True"` inside the box, and
follow the isolation rule below.

New code gets a unit test, and a bug fix gets a test that fails without the fix.

## Don't run things against your real home

Test hosts build the real service graph. That writes `.desktop` files and `mimeapps.list`
entries, and can reach the compositor, so an unguarded run can change your `nxm://` handler or
open windows on your desktop. `test.sh` points every `XDG_*` directory at a throwaway
directory and unsets `DISPLAY` and `WAYLAND_DISPLAY`. Always go through it. If you must run
`dotnet test` or the app by hand, set those variables yourself first, and never start the GUI
against your real home directory.

## One build at a time

MSBuild starts a worker per CPU thread, and two builds at once multiply that until the
machine runs out of memory. `Directory.Build.rsp` turns node reuse off so workers exit with
their build, but that only helps if you don't start a second build while the first is running.
If a build was killed, check for leftovers with `pgrep -fc '[M]SBuild.dll'`; the count should
be 0.

## Commits

- The subject says what changed, in the imperative: "Remove Xbox Game Pass, Origin and EA
  Desktop store support", not "Cleanup".
- When a commit contains several changes, list each one in the body: the file or mechanism and
  what it now does.
- Keep formatting-only changes in their own commit.
- Describe the change, not the plan around it. Leave out task codes, milestone names and
  things that are only true today, like "not pushed yet".

## Scope

This app only runs on Linux, so don't add macOS or Windows host code. Games are still Windows
binaries run under Proton or Wine, so code that reasons about Windows paths, Wine prefixes,
`.exe` files or a prefix's registry belongs here.
