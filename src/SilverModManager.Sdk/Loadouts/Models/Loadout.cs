using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Library;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Sdk.Loadouts;

/// <summary>
/// A loadout can be thought of as a mod list that is specific to a certain
/// installation of a game.
/// </summary>
public partial class Loadout : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.Loadout";

    /// <summary>
    /// The human friendly name for this loadout.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name)) { IsIndexed = true };

    /// <summary>
    /// A capital one-letter name for the loadout, for spine labels and other places where space is limited.
    /// </summary>
    public static readonly StringAttribute ShortName = new(Namespace, nameof(ShortName)) { IsIndexed = true, DefaultValue = "-"};
    
    /// <summary>
    /// The locator ids for the game this loadout is tied to, for gog this will be the build ids, for example. 
    /// </summary>
    public static readonly LocatorIdsAttribute LocatorIds = new(Namespace, nameof(LocatorIds));

    /// <summary>
    /// The game version that this loadout is tied to, based on the LocatorIds.
    /// </summary>
    public static readonly VanityVersionAttribute GameVersion = new(Namespace, nameof(GameVersion));

    /// <summary>
    /// Unique installation of the game this loadout is tied to.
    /// </summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation));

    /// <summary>
    /// A revision number for this loadout. Each change to a file/mod in the loadout should increment
    /// this value by one. This will then be used by the UI and other parts of the app to determine
    /// what aspects of the loadout have changed and need to be reloaded
    /// </summary>
    public static readonly UInt64Attribute Revision = new(Namespace, nameof(Revision));

    /// <summary>
    /// Defines the 'type' of layout that this layout represents.
    /// Currently it is just `Default`, 'Deleted' and `VanillaState` type, with
    /// `marker` being a special hidden loadout type that represents
    /// a game's base state as it was added to the App.
    /// </summary>
    public static readonly EnumByteAttribute<LoadoutKind> LoadoutKind = new(Namespace, nameof(LoadoutKind));

    /// <summary>
    /// DateTime when the loadout was last applied.
    /// Returns DateTime.MinValue if the loadout has never been applied.
    /// </summary>
    public static readonly TimestampAttribute LastAppliedDateTime = new(Namespace, nameof(LastAppliedDateTime))
    {
        IsOptional = true,
        DefaultValue = DateTimeOffset.MinValue,
    };

    public partial struct ReadOnly
    {
        public IGameData Game => TryGetGame(out var game)
            ? game
            : throw new InvalidOperationException($"Loadout `{Name}` is on an installation whose game is not known to this build");

        /// <summary>
        /// Finds the game this loadout is for by the game's own id. Fails for an installation that has no game id
        /// (written before installations were keyed by it, and not recognised since) or whose game is not registered.
        /// </summary>
        public bool TryGetGame([NotNullWhen(true)] out IGameData? game)
        {
            game = null;
            if (!GameInstallMetadata.Game.TryGetValue(Installation, out var gameId)) return false;

            game = Db.Connection.ServiceProvider.GetServices<IGameData>().FirstOrDefault(candidate => candidate.GameId == gameId);
            return game is not null;
        }

        /// <summary>
        /// Get the game installation for this loadout.
        /// </summary>
        public GameInstallation InstallationInstance
        {
            get
            {
                var gameRegistry = Db.Connection.ServiceProvider.GetRequiredService<IGameRegistry>();
                return gameRegistry.ForceGetInstallation(this);
            }
        }

        /// <summary>
        /// Returns true if the loadout should be visible to the user.
        /// A non-visible loadout should be treated as if it doesn't exist.
        /// </summary>
        /// <remarks>
        /// Note(sewer), it's better to 'opt into' functionality, than opt out.
        /// especially, when it comes to displaying elements the user can edit.
        /// </remarks>
        public bool IsVisible() => LoadoutKind == LoadoutKind.Default;
    }
}

