using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.CLI.Types.IpcHandlers;

/// <summary>
/// A file the user sent to the app with an nxm link that no modlist plan expects. The link names a Nexus Mods game,
/// which sibling games share (Skyrim SE and Skyrim VR), so the game comes from the loadouts: the one game of that
/// Nexus id that has one, or, when several do, all of them marked shared so the row can be trimmed. A link that
/// matches no loadout is unknown. A plan that expects the file names its game with a <see cref="GameAcquisition"/>.
/// </summary>
/// <remarks>Never every game of the Nexus id regardless of loadouts: that guess is for the migration only.</remarks>
[PublicAPI]
public sealed record NxmAcquisition(NexusModsGameId NexusGame) : IAcquisitionContext
{
    /// <inheritdoc />
    public Destination Destination(IDb db)
    {
        var games = Loadout.All(db)
            .Select(loadout => loadout.Installation)
            .Where(installation => GameInstallMetadata.GameId.TryGetValue(installation, out var nexusId) && nexusId == NexusGame)
            .Select(installation => GameInstallMetadata.Game.TryGetValue(installation, out var game) ? (GameId?)game : null)
            .OfType<GameId>()
            .ToHashSet();

        return games.Count switch
        {
            0 => Sdk.Library.Destination.Unknown,
            1 => Sdk.Library.Destination.Of(games.Single()),
            _ => new Destination(games, Shared: true),
        };
    }
}
