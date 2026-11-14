using SilverModManager.Abstractions.Library;
using SilverModManager.Library.ModIo;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// A newer modfile of one mod.io mod and the library items downloaded from older ones.
/// </summary>
internal sealed record ModIoModUpdate : ModUpdate
{
    public uint GameId { get; }
    public uint ModId { get; }

    /// <summary>The mod as it reads now.</summary>
    public ModIoMod Mod { get; }

    /// <summary>The modfile to fetch: the mod's current one, which the library does not hold.</summary>
    public ModIoFile File { get; }

    private ModIoModUpdate(uint gameId, ModIoMod mod, ModIoFile file, IReadOnlyList<LibraryItemId> items, string current)
        : base(items, current, ModIoFiles.Newest(mod, file), DateTimeOffset.FromUnixTimeSeconds(mod.DateUpdated))
    {
        GameId = gameId;
        ModId = mod.Id;
        Mod = mod;
        File = file;
    }

    /// <summary>None when there are no items.</summary>
    public static ModIoModUpdate? From(uint gameId, ModIoMod mod, ModIoFile file, IReadOnlyList<(LibraryItem.ReadOnly Item, string Version)> items)
        => items.Count == 0
            ? null
            : new ModIoModUpdate(gameId, mod, file, [.. items.Select(static item => LibraryItemId.From(item.Item.Id))], items[0].Version);
}
