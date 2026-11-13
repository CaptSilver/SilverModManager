using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>A mod folder the loadout installs: the group that put it there and the manifest it carries, if any.</summary>
internal sealed record ManagedModEntry(string Folder, ModManifest? Manifest, LoadoutItemGroup.ReadOnly Group)
{
    /// <summary>What the order file lists it under.</summary>
    public string Name => Manifest?.Id ?? Folder;
}

/// <summary>
/// The mod folders a loadout installs, read from the loadout's own rows, for the diagnostics that cannot wait for an
/// apply to have put the files on disk. A folder the app filed under Overrides is the game's or the player's, not the
/// loadout's, so it is left out, as it is from the order file.
/// </summary>
internal static class ManagedModList
{
    /// <summary>The mod folder a file of the game folder belongs to; the order file itself belongs to none.</summary>
    internal static string? ModOf(LoadOrderFileGameDefinition definition, GamePath file)
    {
        var modFolder = new GamePath(LocationId.Game, definition.ModFolder);
        if (file == definition.OrderFile.ToGamePath() || !file.InFolder(modFolder)) return null;

        return definition.Format.ModOf(file.Path.RelativeTo(modFolder.Path));
    }

    /// <summary>The enabled mod folders, loser first (the order the order file lists them in).</summary>
    internal static async Task<IReadOnlyList<ManagedModEntry>> Read(
        LoadOrderFileGameDefinition definition, Loadout.ReadOnly loadout, IFileStore fileStore, CancellationToken cancellationToken)
    {
        var overrides = OverridesGroupLookup.Find(loadout);
        var priorities = ModUnitPriorities.Read(loadout, file => ModOf(definition, file));
        var groups = new Dictionary<string, LoadoutItemGroup.ReadOnly>(StringComparer.Ordinal);
        var manifests = new Dictionary<string, ModManifest?>(StringComparer.Ordinal);

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            if (!item.IsEnabled() || !item.HasParent() || OverridesGroupLookup.IsInGroup(loadout.Db, item.Id, overrides)) continue;

            var path = (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath;
            if (ModOf(definition, path) is not { } folder) continue;

            groups.TryAdd(folder, item.Parent);
            if (definition.ManifestReader is { } reader && path == new GamePath(LocationId.Game, definition.ModFolder / folder / reader.ManifestPath))
                manifests[folder] = reader.Read(await fileStore.Load(file.Identity, cancellationToken));
        }

        return groups
            .OrderBy(entry => priorities.GetValueOrDefault(entry.Key))
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new ManagedModEntry(entry.Key, manifests.GetValueOrDefault(entry.Key), entry.Value))
            .ToList();
    }
}
