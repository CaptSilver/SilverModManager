using SilverModManager.Abstractions.Library;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// A newer file of one GameBanana mod and the library items downloaded from older ones.
/// </summary>
internal sealed record GameBananaModUpdate : ModUpdate
{
    public long ModId { get; }

    /// <summary>The mod's page as it reads now.</summary>
    public GameBananaProfile Profile { get; }

    /// <summary>The file to fetch: the newest one the library does not hold.</summary>
    public GameBananaFile File { get; }

    private GameBananaModUpdate(long modId, GameBananaProfile profile, GameBananaFile file, IReadOnlyList<LibraryItemId> items, string current)
        : base(items, current, GameBananaVersions.Newest(profile), DateTimeOffset.FromUnixTimeSeconds(profile.UpdateStamp))
    {
        ModId = modId;
        Profile = profile;
        File = file;
    }

    /// <summary>None when there are no items.</summary>
    public static GameBananaModUpdate? From(long modId, GameBananaProfile profile, GameBananaFile file, IReadOnlyList<(LibraryItem.ReadOnly Item, string Version)> items)
        => items.Count == 0
            ? null
            : new GameBananaModUpdate(modId, profile, file, [.. items.Select(static item => LibraryItemId.From(item.Item.Id))], items[0].Version);
}

/// <summary>
/// How a mod's version reads when the page may have none.
/// </summary>
internal static class GameBananaVersions
{
    /// <summary>The page's version, or when it was last updated, which is all some pages say.</summary>
    public static string Newest(GameBananaProfile profile)
        => string.IsNullOrWhiteSpace(profile.Version) ? UpdatedOn(profile.UpdateStamp) : profile.Version;

    /// <summary>"updated on 2021-06-03", the date in UTC.</summary>
    public static string UpdatedOn(long stamp) => $"updated on {DateTimeOffset.FromUnixTimeSeconds(stamp):yyyy-MM-dd}";

    /// <summary>The version the file was downloaded at: its page's version, or the update date when it had none.</summary>
    public static string Held(GameBananaLibraryFile.ReadOnly file)
        => GameBananaLibraryFile.ListedVersion.TryGetValue(file, out var version) && !string.IsNullOrWhiteSpace(version)
            ? version
            : file.DateUpdated > 0 ? UpdatedOn(file.DateUpdated) : "";
}
