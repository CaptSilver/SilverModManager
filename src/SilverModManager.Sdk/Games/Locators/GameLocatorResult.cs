using System.Collections.Immutable;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

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

    /// <summary>
    /// What the game runs on, for a Windows build found on Linux, or null for a native build or a locator
    /// that does not know.
    /// </summary>
    /// <remarks>
    /// <see cref="Store"/> says where the game was bought; this says which prefix and runner it lives in.
    /// A GOG game can be found by Heroic or inside a plain Wine prefix, and the two are launched differently.
    /// </remarks>
    public CompatibilityLayer? Compatibility { get; init; }

    /// <summary>
    /// The store client's own install directory, when the locator knows it. For Steam this is the
    /// directory holding <c>steamapps/</c> and <c>depotcache/</c>.
    /// </summary>
    public AbsolutePath? StoreRoot { get; init; }

    /// <summary>
    /// The store's build identifier for the installed files (Steam's appmanifest <c>buildid</c>), or null
    /// when the store has none.
    /// </summary>
    public string? StoreBuildId { get; init; }

    /// <summary>
    /// The build the store is moving the game to (Steam's appmanifest <c>TargetBuildID</c>), or null when
    /// it has no update target. Equal to <see cref="StoreBuildId"/> when the game is current, which Steam
    /// also reports for installs it left flagged as updating, so compare the two rather than testing this
    /// alone.
    /// </summary>
    public string? StoreTargetBuildId { get; init; }

    /// <summary>
    /// The store reports the installed files are being updated or need an update, so they may not match
    /// the build the store lists.
    /// </summary>
    public bool StoreUpdatePending { get; init; }

    /// <summary>
    /// A time the store's last write to the installed files cannot be later than, or null when the locator
    /// has no such bound. For Steam, the appmanifest's update time.
    /// </summary>
    public DateTimeOffset? StoreLastWrite { get; init; }

    /// <summary>
    /// True when the store installs an update as soon as one is out, which is Steam's default. False when
    /// the user chose to update only on launch, or the store has no such setting.
    /// </summary>
    public bool StoreUpdatesInBackground { get; init; }
}
