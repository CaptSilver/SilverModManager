using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Inference;

/// <summary>A Thunderstore package is for the game whose community it was listed under.</summary>
public sealed class ThunderstoreCommunityInference(IEnumerable<IGameData> games) : IDestinationInference
{
    /// <inheritdoc />
    public int Priority => 30;

    /// <inheritdoc />
    public Destination Infer(IDb db, LibraryItem.ReadOnly item)
    {
        var package = ThunderstoreLibraryFile.Load(db, item.Id);
        if (!package.IsValid()) return Destination.Unknown;

        var matches = games
            .Where(game => ThunderstoreGame.For(game)?.ThunderstoreCommunity == package.Community)
            .Select(game => game.GameId)
            .ToHashSet();
        return matches.Count == 0 ? Destination.Unknown : new Destination(matches, Shared: false);
    }
}
