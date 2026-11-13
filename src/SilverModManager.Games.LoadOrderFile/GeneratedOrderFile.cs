using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// A game's order file, rendered on every Apply from the mods the sync leaves on disk and the loadout's priorities,
/// loser first. The loadout is the order: an edit made to the file by hand is not read back, and the next Apply
/// replaces it.
/// </summary>
/// <remarks>
/// Mods in a folder the app does not manage (<see cref="LoadOrderFileGameDefinition.ExternalMods"/>) are the one
/// exception: the ones the file on disk already names stay in it, ahead of the loadout's mods, and each source's
/// <see cref="ExternalListing"/> says whether it lists more. Rendering reads that file and the folders. An apply has
/// already deleted and extracted the loadout's files when it writes this one, and the Apply button's status renders
/// it only when nothing else is pending, so both see the folders as the apply leaves them (see
/// <see cref="IIntrinsicFileRenderedFromLoadout"/>).
/// </remarks>
public sealed class GeneratedOrderFile(LoadOrderFileGameDefinition definition, ILogger<GeneratedOrderFile> logger) : IIntrinsicFileRenderedFromLoadout
{
    public GamePath Path { get; } = definition.OrderFile.ToGamePath();

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var text = await Render(loadout, syncTree);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
    }

    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        var generated = await Render(loadout, syncTree);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var onDisk = await reader.ReadToEndAsync();
        if (onDisk == generated) return;

        logger.LogInformation("The order file {Path} differed from the one the loadout generates and is replaced. Changes made to it by hand are not kept; reorder the mods on the Load Order page instead", Path);
    }

    private async Task<string> Render(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var locations = loadout.InstallationInstance.Locations;
        var gameFolder = locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        var file = locations.ToAbsolutePath(Path);
        var overrides = OverridesGroupLookup.Find(loadout);
        var managed = ManagedFolders(syncTree, ModUnitPriorities.Read(loadout, ModOf), item => OverridesGroupLookup.IsInGroup(loadout.Db, item, overrides));

        return Render(gameFolder, file.FileExists ? await file.ReadAllTextAsync() : null, managed);
    }

    /// <summary>
    /// The folders of the mod folder that the loadout installs and leaves on disk, loser first. A folder that was
    /// there before the loadout (the game's own state, or one the player added) is not the loadout's to switch on, and
    /// stays that way after an apply files it under Overrides. A file no loadout row names still carries the enum's
    /// default source type, which is why a loadout part is required too.
    /// </summary>
    internal IReadOnlyList<string> ManagedFolders(
        Dictionary<GamePath, SyncNode> syncTree, IReadOnlyDictionary<string, ulong> priorities, Func<EntityId, bool> isInOverrides)
        => syncTree
            .Where(entry => IsInstalledByLoadout(entry.Value, isInOverrides))
            .Select(entry => ModOf(entry.Key))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mod => priorities.GetValueOrDefault(mod))
            .ThenBy(mod => mod, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A file of a mod, as opposed to one the game or the player left in the folder: the loadout has a row for it, it
    /// is not a loadout row the app made of a file it found on disk (the Overrides group), and it stays on disk.
    /// </summary>
    private static bool IsInstalledByLoadout(in SyncNode node, Func<EntityId, bool> isInOverrides)
        => node is { WillBeOnDisk: true, HaveLoadout: true, SourceItemType: LoadoutSourceItemType.Loadout }
           && node.Loadout.HaveEntityId
           && !isInOverrides(node.Loadout.EntityId);

    /// <summary>A mod folder the loadout installs, with the manifest it carries when it has one.</summary>
    internal sealed record ManagedMod(string Folder, ModManifest? Manifest)
    {
        /// <summary>What the order file lists it under: its manifest's id when the folder still has one, else the folder's name.</summary>
        public string Name => Manifest?.Id ?? Folder;
    }

    /// <summary>The file's text for <paramref name="managedFolders"/> and the external mods the game folder holds.</summary>
    /// <param name="gameFolder">The game folder, where the external sources and the managed mods' manifests are looked for.</param>
    /// <param name="currentFileText">The order file on disk, or null when there is none.</param>
    /// <param name="managedFolders">The loadout's mod folders, loser first.</param>
    internal string Render(AbsolutePath gameFolder, string? currentFileText, IReadOnlyList<string> managedFolders)
        => definition.Format.Render(Order(gameFolder, currentFileText, managedFolders.Select(folder => new ManagedMod(folder, ManifestOnDisk(gameFolder, folder))).ToList()), currentFileText);

    /// <summary>
    /// The names the file lists, loser first: the external mods the file keeps, then the managed mods, except that a
    /// managed mod whose manifest says it loads before an external mod goes directly before the first one it names.
    /// A mod is placed that way only against the external mods, never another managed mod, whose order is the loadout's.
    /// </summary>
    internal IReadOnlyList<string> Order(AbsolutePath gameFolder, string? currentFileText, IReadOnlyList<ManagedMod> managedMods)
    {
        var managed = managedMods.DistinctBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var order = ExternalNames(gameFolder, currentFileText, managed.Select(mod => mod.Name).ToList()).ToList();
        var externals = order.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tail = new List<string>();
        foreach (var mod in managed)
        {
            var before = mod.Manifest is { } manifest ? manifest.LoadBefore.Concat(manifest.ForceLoadBefore).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            var target = before.Count == 0 ? -1 : order.FindIndex(name => externals.Contains(name) && before.Contains(name));
            if (target >= 0) order.Insert(target, mod.Name);
            else tail.Add(mod.Name);
        }

        return [.. order, .. tail];
    }

    /// <summary>The manifest a managed folder carries on disk, or null when it has none, or no reader exists for the game.</summary>
    private ModManifest? ManifestOnDisk(AbsolutePath gameFolder, string folder)
    {
        if (definition.ManifestReader is not { } reader) return null;

        var manifest = gameFolder.Combine(definition.ModFolder).Combine(folder).Combine(reader.ManifestPath);
        return manifest.FileExists ? reader.Read(ManifestNamer.ReadAll(manifest)) : null;
    }

    /// <summary>
    /// The external mods the file lists, in the file's order, minus any the loadout installs itself. A mod the file
    /// names that no source finds is gone, and goes from the file. Mods a source lists unconditionally and the file
    /// does not name come first; mods listed only because there is no file come last.
    /// </summary>
    private IReadOnlyList<string> ExternalNames(AbsolutePath gameFolder, string? currentFileText, IReadOnlyList<string> managed)
    {
        var listed = currentFileText is null ? [] : definition.Format.Listed(currentFileText);
        var managedNames = managed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // First source to find a name decides its policy.
        var found = new Dictionary<string, ExternalListing>(StringComparer.OrdinalIgnoreCase);
        var foundOrder = new List<string>();
        foreach (var source in definition.ExternalMods)
        {
            foreach (var name in source.Find(gameFolder))
            {
                if (managedNames.Contains(name) || !found.TryAdd(name, source.Listing)) continue;
                foundOrder.Add(name);
            }
        }

        var inFile = listed.Where(found.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inFileSet = inFile.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = foundOrder.Where(name => !inFileSet.Contains(name)).ToList();

        var front = unlisted.Where(name => found[name] == ExternalListing.Always);
        var tail = currentFileText is null ? unlisted.Where(name => found[name] == ExternalListing.WhenListedOrNoFile) : [];
        return [.. front, .. inFile, .. tail];
    }

    private string? ModOf(GamePath file) => ManagedModList.ModOf(definition, file);
}
