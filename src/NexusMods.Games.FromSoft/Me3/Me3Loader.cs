using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The me3 mod loader of a game. The constructor only stores its arguments: the settings, the file system and the
/// version probe are fetched from the provider when a member is called, so a test that replaces one of them in the
/// container is honoured.
/// </summary>
public sealed class Me3Loader(IServiceProvider provider, Me3LoaderOptions options, FromSoftGameDefinition definition) : IFromSoftModLoader
{
    /// <summary>The profile text of the game: its me3 name, modded save and overlay natives.</summary>
    public Me3ProfileSpec ProfileSpec => new(options.GameName, definition.SaveNames.Modded, options.OverlayNatives);

    /// <summary>Whether the game loads its mods with me3.</summary>
    public static bool Serves(IGameData game) => game is IFromSoftGame { Loader: Me3Loader };

    /// <summary>The folder mod packages and natives of one installation deploy to.</summary>
    public static AbsolutePath ModsFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => FromSoftPaths.InstallationRoot(layout, settings, fileSystem, installationFolder) / "mods";

    /// <summary>The folder the me3 release of one installation deploys to.</summary>
    public static AbsolutePath HostFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => FromSoftPaths.InstallationRoot(layout, settings, fileSystem, installationFolder) / "me3";

    // The ids are persisted in every deployed loadout, so every me3 game uses these two.
    public ImmutableDictionary<LocationId, AbsolutePath> Locations(FromSoftSettings settings, IFileSystem hostFileSystem, string installationFolder)
        => ImmutableDictionary<LocationId, AbsolutePath>.Empty
            .Add(LocationId.Me3Mods, ModsFolder(definition.Layout, settings, hostFileSystem, installationFolder))
            .Add(LocationId.Me3Host, HostFolder(definition.Layout, settings, hostFileSystem, installationFolder));

    public IEnumerable<IIntrinsicFile> IntrinsicFiles()
        => [new Me3ProfileFile(provider.GetRequiredService<ILogger<Me3ProfileFile>>(), this)];

    public ILibraryItemInstaller HostInstaller() => new Me3HostInstaller(provider);

    public IEnumerable<ILibraryItemInstaller> ModInstallers() => [new Me3PackageInstaller(provider), new Me3NativeInstaller(provider)];

    /// <summary>The me3 verb and the profile flag.</summary>
    public static readonly string[] Me3Launch = ["launch", "-p"];

    /// <summary>Names the game to launch. Unverified against me3 0.13.</summary>
    public static string[] GameSelection(string gameName) => ["-g", gameName];

    /// <summary>Where the Windows halves of me3 are. Unverified against me3 0.13.</summary>
    public static readonly string[] WindowsBinariesFlag = ["--windows-binaries-dir"];

    /// <summary>
    /// The folder that stands in for the user's home for me3: its cache, profiles, logs and data are kept here
    /// instead of in <c>~/.cache/me3</c>, <c>~/.config/me3</c> and <c>~/.local/share/me3</c>.
    /// </summary>
    public static AbsolutePath HomeFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => FromSoftPaths.InstallationRoot(layout, settings, fileSystem, installationFolder) / "me3-home";

    /// <summary>The four XDG folders inside the home, which are the only places me3 may write outside the game's own folders.</summary>
    public static AbsolutePath[] HomeFolders(AbsolutePath home) => [home / "cache", home / "config", home / "data", home / "state"];

    /// <summary>The XDG variables that point me3 at the home.</summary>
    public static Dictionary<string, string> HomeEnvironment(AbsolutePath home) => new()
    {
        ["XDG_CACHE_HOME"] = (home / "cache").GetFullPath(),
        ["XDG_CONFIG_HOME"] = (home / "config").GetFullPath(),
        ["XDG_DATA_HOME"] = (home / "data").GetFullPath(),
        ["XDG_STATE_HOME"] = (home / "state").GetFullPath(),
    };

    private AbsolutePath HomeOf(GameInstallation installation)
        => HomeFolder(definition.Layout, provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), provider.GetRequiredService<IFileSystem>(),
            FromSoftPaths.InstallationFolderName(definition.Layout, installation.LocatorResult));

    private static AbsolutePath BinaryOf(GameInstallation installation) => installation.Locations[LocationId.Me3Host].Path / Me3HostFiles.Binary;

    public string MissingHostMessage(string loadoutName)
        => $"Loadout '{loadoutName}' has mods but no me3, so there is nothing to load them with. Install the me3 Linux release into it.";

    public Veto? CheckHost(GameInstallation installation, string loadoutName)
    {
        var me3 = BinaryOf(installation);
        return me3.FileExists
            ? null
            : new Veto($"Cannot start {installation.Game.DisplayName} with mods: me3 is not deployed ({me3} is missing). Install the me3 Linux release into loadout '{loadoutName}' and apply it.");
    }

    public void PrepareHost(GameInstallation installation)
    {
        // me3's home is shared into the game's container, which can only share a folder that already exists.
        foreach (var folder in HomeFolders(HomeOf(installation))) folder.CreateDirectory();
        Me3HostFiles.EnsureExecutable(BinaryOf(installation));
    }

    public IReadOnlyList<string> LaunchCommand(GameInstallation installation)
    {
        var host = installation.Locations[LocationId.Me3Host].Path;
        return
        [
            BinaryOf(installation).GetFullPath(),
            ..Me3Launch, (installation.Locations[LocationId.Me3Mods].Path / Me3ModsLayout.ProfileFileName).GetFullPath(),
            ..GameSelection(options.GameName),
            ..WindowsBinariesFlag, (host / "bin/win64").GetFullPath(),
        ];
    }

    public IReadOnlyDictionary<string, string> LaunchEnvironment(GameInstallation installation) => HomeEnvironment(HomeOf(installation));

    public IEnumerable<AbsolutePath> WritableRoots(GameInstallation installation)
        =>
        [
            installation.Locations[LocationId.Me3Mods].Path,
            installation.Locations[LocationId.Me3Host].Path,
            HomeOf(installation),
        ];

    // Resolved here and not in the constructor, so a version probe replaced in the container is the one that runs.
    public CheckResult? HostCheck(GameInstallation installation)
    {
        var binary = BinaryOf(installation);
        var executable = Me3HostFiles.IsExecutable(binary);
        return HostCheckResult(binary, executable, executable ? provider.GetRequiredService<Me3VersionProbe>().Read(binary) : null);
    }

    /// <param name="binary">Where the me3 binary is deployed.</param>
    /// <param name="executable">Whether it is there and executable.</param>
    /// <param name="version">What it printed for <c>--version</c>; null when it was not run or printed nothing.</param>
    public static CheckResult HostCheckResult(AbsolutePath binary, bool executable, string? version)
    {
        if (!executable)
            return new CheckResult("me3", CheckLevel.Warn, $"me3 is not deployed or not executable ({binary}). Only a loadout with mods needs it.");

        return string.IsNullOrWhiteSpace(version)
            ? new CheckResult("me3", CheckLevel.Warn, $"me3 at {binary} did not report a version: it failed to start, exited with an error or printed nothing.")
            : new CheckResult("me3", CheckLevel.Ok, $"{version.Trim()} ({binary})");
    }

    public AbsolutePath ConfigurationLayer(GameInstallation installation)
        => installation.Locations[LocationId.Me3Mods].Path / Me3ModsLayout.OverlayFolder;

    /// <summary>True when any enabled file of the loadout sits in the mods folder: a package, a native or an overlay file.</summary>
    public bool IsModded(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Any(path => path.LocationId == LocationId.Me3Mods);

    /// <summary>True when the loadout has the me3 binary, enabled, in the me3 host location.</summary>
    public bool HasHost(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Contains(new GamePath(LocationId.Me3Host, Me3HostFiles.Binary));

    private static IEnumerable<GamePath> EnabledTargets(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled())
            .Select(item => (GamePath)item.TargetPath);
}
