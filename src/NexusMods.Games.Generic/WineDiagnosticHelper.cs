using System.Collections.Immutable;
using System.Text;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Generic;

public static class WineDiagnosticHelper
{
    public static async ValueTask<string?> GetWinetricksInstructions(GameInstallation gameInstallation, ImmutableHashSet<string> requiredPackages, CancellationToken cancellationToken = default)
    {
        var locatorResultMetadata = gameInstallation.LocatorResult;
        var linuxCompatibilityDataProvider = locatorResultMetadata.LinuxCompatabilityDataProvider;
        if (linuxCompatibilityDataProvider is null) return null;

        var installedPackages = await linuxCompatibilityDataProvider.GetInstalledWinetricksComponents(cancellationToken: cancellationToken);

        var missingPackages = requiredPackages.Except(installedPackages);
        if (missingPackages.Count == 0) return null;

        var sb = new StringBuilder();

        var missingPackagesString = missingPackages.Select(x => $"* `{x}`").Aggregate((a, b) => $"{a}\n{b}");

        if (locatorResultMetadata.Store == GameStore.Steam)
        {
            sb.AppendLine($"""
Use [protontricks](https://github.com/Matoking/protontricks) to install the following missing required packages:

{missingPackagesString}
""");
        }
        else
        {
            sb.AppendLine($"""
Use [winetricks](https://github.com/Winetricks/winetricks) to install the following missing required packages:

{missingPackagesString}
""");
        }

        return sb.ToString();
    }

    public static async ValueTask<string?> GetWineDllOverridesUpdateInstructions(GameInstallation gameInstallation, IReadOnlyList<WineDllOverride> requiredOverrides, IReadOnlyList<string> launcherSkipArguments, CancellationToken cancellationToken = default)
    {
        if (requiredOverrides.Count == 0) return null;

        var locatorResultMetadata = gameInstallation.LocatorResult;

        var linuxCompatibilityDataProvider = locatorResultMetadata.LinuxCompatabilityDataProvider;
        if (linuxCompatibilityDataProvider is null) return null;

        var existingOverrides = await linuxCompatibilityDataProvider.GetWineDllOverrides(cancellationToken: cancellationToken);

        var overridesToAdd = new List<WineDllOverride>();
        var overridesToUpdate = new List<(WineDllOverride From, WineDllOverride To)>();

        foreach (var requiredOverride in requiredOverrides)
        {
            if (existingOverrides.TryGetFirst(dllOverride => dllOverride.DllName.Equals(requiredOverride.DllName, StringComparison.OrdinalIgnoreCase), out var found))
            {
                if (found.OverrideTypes.SequenceEqual(requiredOverride.OverrideTypes)) continue;
                overridesToUpdate.Add((From: found, To: requiredOverride));
            }
            else
            {
                overridesToAdd.Add(requiredOverride);
            }
        }

        if (overridesToAdd.Count == 0 && overridesToUpdate.Count == 0) return null;

        var sb = new StringBuilder();

        var dllOverridesString = WineParser.MergeDllOverrides("", requiredOverrides);
        const string variable = WineParser.WineDllOverridesEnvironmentVariableName;
        var launcherSkip = launcherSkipArguments.Count > 0 ? " " + string.Join(' ', launcherSkipArguments) : "";
        if (locatorResultMetadata.Store == GameStore.Steam)
        {
            sb.AppendLine($"""
Open the loadout card's "Use this loadout from Steam" and write the line while Steam is closed, or run `silvermod steam-launch-option --loadout <name> --write`. The line it writes sets `{variable}` to `{dllOverridesString}`.

To do it by hand instead:

* Open Steam
* Right-click the game
* Click on "Properties..."
* Open the "General" section
* Update "Launch Options" to be the following:

```
{variable}="{dllOverridesString}" %command%{launcherSkip}
```
""");
        } else if (locatorResultMetadata.Store == GameStore.GOG)
        {
            sb.AppendLine($"""
* Open the Heroic Games Launcher
* Right-click the game
* Click on "Settings"
* Go to the "Advanced" tab
* Update the environment variables:

```
{variable}="{dllOverridesString}"
```
""");
        }
        else
        {
            sb.AppendLine($"""
Update the `{variable}` environment variable to be the following:

```
{dllOverridesString}
```
""");
        }

        return sb.ToString();
    }
}
