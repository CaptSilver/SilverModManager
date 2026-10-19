using System.Collections.Immutable;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

[PublicAPI]
public class GameLocatorResult
{
    /// <summary>
    /// The game that was located.
    /// </summary>
    public required IGameData Game { get; init; }

    /// <summary>
    /// The path of the game.
    /// </summary>
    /// <remarks>
    /// The file system of this path is the game's view of the machine, not the host's. For a game
    /// running under Wine or Proton it maps the Windows known folders (documents, local app data)
    /// into the prefix. Ask it for known paths only to resolve game locations; host folders come from
    /// the host file system.
    /// </remarks>
    public required AbsolutePath Path { get; init; }

    /// <summary>
    /// The store used to install the game.
    /// </summary>
    public required GameStore Store { get; init; }

    /// <summary>
    /// The opaque store identifier for the game.
    /// </summary>
    public required string StoreIdentifier { get; init; }

    /// <summary>
    /// The locator identifiers for the installation.
    /// </summary>
    public required ImmutableArray<LocatorId> LocatorIds { get; init; }

    /// <summary>
    /// The locator that found this instance.
    /// </summary>
    public required IGameLocator Locator { get; init; }

    /// <summary>
    /// The platform of the game.
    /// </summary>
    /// <remarks>
    /// Defaults to a native Linux build. Windows versions run through WINE/Proton set this to Windows.
    /// </remarks>
    public OSPlatform Platform { get; init; } = OSPlatform.Linux;

    /// <summary>
    /// Target operating system.
    /// </summary>
    public IOSInformation TargetOS => new OSInformation(Platform);

    /// <summary>
    /// Optional linux compatability data provider.
    /// </summary>
    public ILinuxCompatabilityDataProvider? LinuxCompatabilityDataProvider { get; init; }

    /// <summary>
    /// The Wine prefix directory (the <c>pfx</c> directory for Proton) the game's known folders
    /// resolve into, or null for a native Linux build.
    /// </summary>
    /// <remarks>
    /// The directory may not exist yet: a game that was never launched has no prefix. Use
    /// <see cref="ProtonPrefix.IsReady"/> to tell.
    /// </remarks>
    public AbsolutePath? WinePrefixPath { get; init; }
}
