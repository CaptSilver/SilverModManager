using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
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
/// exception: the ones the file on disk already names stay in it, ahead of the loadout's mods. Rendering reads that
/// file and the folder, which an apply never changes before it writes this file, so the Apply button's status can
/// still render it on its own (see <see cref="IIntrinsicFileRenderedFromLoadout"/>).
/// </remarks>
public sealed class GeneratedOrderFile(LoadOrderFileGameDefinition definition, ILogger<GeneratedOrderFile> logger) : IIntrinsicFileRenderedFromLoadout
{
    public GamePath Path { get; } = new(LocationId.Game, definition.OrderFile);

    private GamePath ModFolder => new(LocationId.Game, definition.ModFolder);

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
        var priorities = ModUnitPriorities.Read(loadout, ModOf);
        var mods = syncTree
            .Where(entry => entry.Value.WillBeOnDisk)
            .Select(entry => ModOf(entry.Key))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mod => priorities.GetValueOrDefault(mod))
            .ThenBy(mod => mod, StringComparer.Ordinal)
            .ToList();

        var external = await ExternalModsStillListed(loadout, mods);
        return definition.Format.Render([.. external, .. mods]);
    }

    /// <summary>
    /// The external mods the file on disk names that the game folder still has, in the file's order, minus any the
    /// loadout installs itself. A mod the file names that is neither is gone, and goes from the file.
    /// </summary>
    private async Task<IReadOnlyList<string>> ExternalModsStillListed(Loadout.ReadOnly loadout, IReadOnlyList<string> managed)
    {
        if (definition.ExternalMods is not { } source) return [];

        var locations = loadout.InstallationInstance.Locations;
        var present = source.Find(locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var file = locations.ToAbsolutePath(Path);
        if (present.Count == 0 || !file.FileExists) return [];

        var listed = definition.Format.Listed(await file.ReadAllTextAsync());
        var managedNames = managed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return listed.Where(mod => present.Contains(mod) && !managedNames.Contains(mod)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The mod a file in the game folder belongs to; the order file itself belongs to none.</summary>
    private string? ModOf(GamePath file)
    {
        if (file == Path || !file.InFolder(ModFolder)) return null;

        return definition.Format.ModOf(file.Path.RelativeTo(ModFolder.Path));
    }
}
