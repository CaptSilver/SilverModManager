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
}
