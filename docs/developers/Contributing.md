## Code Contributions

### Prerequisites

- The latest version of the [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
- An IDE:
    - [JetBrains Rider](https://www.jetbrains.com/rider/) and the [Avalonia Rider Extension](https://plugins.jetbrains.com/plugin/14839-avaloniarider),
    - [Visual Studio](https://visualstudio.microsoft.com/downloads/) and the [Avalonia Visual Studio Extension](https://marketplace.visualstudio.com/items?itemName=AvaloniaTeam.AvaloniaVS).

There are also some [Item Templates](https://github.com/AvaloniaUI/avalonia-dotnet-templates) for Avalonia that can be very useful to have,
helping you make things like new 'Windows' and 'Controls' easier.

### Running the project

- Clone the repository (optionally make a fork first)
- Ensure submodules have been cloned as well, you may use `git submodule update --init --recursive` to do so
- Build and run the `NexusMods.App` project from your IDE to start the app, or use
  `dotnet run --project src/NexusMods.App/NexusMods.App.csproj `

### Writing code

Make sure to follow our [Development Guidelines](./development-guidelines/UICodingGuidelines.md).

### Debugging the App
This app is designed to be used with the games it supports. It is recommended to
have a supported game installed on your system to debug the app with full functionality.

If you want to contribute to the app without a supported game installed, you can continue
doing so by manually adding a supported game. However, supported games that are manually added and not installed properly for 
testing purposes may not be playable. You can still use the app to test the UI and other features, 
but some features may not work as expected. To add a supported game manually:

1. Have a directory ready and copy its path for the game you want to add to store its mods.
2. After building the project, go to your terminal and change the directory to the `src/NexusMods.App/bin/Debug/net10.0` folder.
3. Enter the following CLI command and replace the arguments: 
   ```bash
   NexusMods.App as-main add-game -g "Game Name" -v [Version] -p "Path"
   ```
4. Save the `EntityID` provided and run the app to check if the game is listed in the "My Games" section.
5. Later on, you can remove the game by running the following CLI command:
   ```bash
   NexusMods.App as-main remove-game -g "Game Name" -id "EntityID"
   ```

### Testing with Nexus API Integration

Some tests in the project require a valid Nexus Mods API key to run properly.
These tests are marked with the `RequiresApiKey` trait and will fail if the API key is not set.

#### Obtaining a Nexus API Key

1. Go to your [API Key management page](https://www.nexusmods.com/users/myaccount?tab=api)
2. Generate a new API key for development/testing purposes

#### Setting Up the Environment Variable

To run networking tests locally, set the `NEXUS_API_KEY` environment variable.

## Running tests

All test projects run on [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) (xUnit v3), selected by the `test` section of `global.json`. `dotnet test` therefore takes `--solution` / `--project` rather than a positional path.

```bash
scripts/test.sh                                      # offline suite, whole solution
scripts/test.sh --no-build                           # skip the build step
dotnet test --project tests/NexusMods.CLI.Tests      # one project
```

`scripts/test.sh` excludes tests carrying the `RequiresNetworking`, `RequiresApiKey` and `FlakeyTest` traits. The filters are passed as the `-p:XunitArgs=...` MSBuild property, which `tests/Directory.Build.props` maps onto `TestingPlatformCommandLineArguments`. To run only the networking tests:

```bash
dotnet test --solution NexusMods.App.sln --ignore-exit-code 8 -p:XunitArgs="--filter-trait RequiresNetworking=True"
```

Exit code 8 means "zero tests ran", which is expected for helper projects and for projects whose tests are all filtered out.

## Translations

Translations are currently handled via the IDE. See [this issue](https://github.com/Nexus-Mods/NexusMods.App/issues/598) for more details.

## For Package Maintainers

Packaging for the fork is described in [`CONTRIBUTING.md`](../../CONTRIBUTING.md); `scripts/dev-package.sh`
builds an AppImage with [PupNet](https://github.com/kuiperzone/PupNet-Deploy) from
[`app.pupnet.conf`](../../src/NexusMods.App/app.pupnet.conf).

The app id is `io.github.CaptSilver.SilverModManager`. The desktop entry, AppStream metadata and
icon live next to the project in `src/NexusMods.App`:

- `io.github.CaptSilver.SilverModManager.desktop`: replace `${INSTALL_EXEC}` with the absolute
  path of the binary when you build a package. If `INSTALLATION_METHOD_PACKAGE_MANAGER` is not
  defined, the app writes this file itself at runtime and fills the placeholder in.
- `io.github.CaptSilver.SilverModManager.metainfo.xml`: [AppStream](https://www.freedesktop.org/software/appstream/docs/) metadata.
- `icon.svg`.

Compile constants go in with `-p:DefineConstants="NAME"` on `dotnet build` or `dotnet publish`.
The ones the app reads are in [`Directory.Build.props`](../../Directory.Build.props):

- `SMM_USE_SYSTEM_EXTRACTOR`: use the `7zz` on `PATH` instead of the bundled build.
  Some `7zz` builds cannot read RAR archives, which many mods still use.
- `INSTALLATION_METHOD_PACKAGE_MANAGER`: stops the app from writing its `.desktop` file at runtime.
