using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

[PublicAPI]
public interface ILinuxCompatabilityDataProvider
{
    /// <summary>
    /// Path to the WINE prefix directory.
    /// </summary>
    AbsolutePath WinePrefixDirectoryPath { get; }

    /// <summary>
    /// Path to the Steam installation root that owns the game.
    /// </summary>
    AbsolutePath SteamInstallPath { get; }

    /// <summary>
    /// Path to the <c>localconfig.vdf</c> of the Steam user who last owned the game. The file may not exist.
    /// </summary>
    AbsolutePath SteamLocalConfigPath { get; }

    /// <summary>
    /// Gets all WINE DLL overrides.
    /// </summary>
    ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken);

    /// <summary>
    /// Gets all installed winetricks components.
    /// </summary>
    ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken);
}
