using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// A game's order file, rendered on every Apply from the mods the sync leaves on disk and the loadout's priorities,
/// loser first. The loadout is the order: an edit made to the file by hand is not read back, and the next Apply
/// replaces it.
/// </summary>
public sealed class GeneratedOrderFile(LoadOrderFileGameDefinition definition, ILogger<GeneratedOrderFile> logger) : IIntrinsicFileRenderedFromLoadout
{
    public GamePath Path { get; } = new(LocationId.Game, definition.OrderFile);

    private GamePath ModFolder => new(LocationId.Game, definition.ModFolder);

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var text = Render(loadout, syncTree);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
    }

    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        var generated = Render(loadout, syncTree);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var onDisk = await reader.ReadToEndAsync();
        if (onDisk == generated) return;

        logger.LogInformation("The order file {Path} differed from the one the loadout generates and is replaced. Changes made to it by hand are not kept; reorder the mods on the Load Order page instead", Path);
    }

    private string Render(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var priorities = ModUnitPriorities.Read(loadout, ModOf);
        var mods = syncTree
            .Where(entry => WillBeOnDisk(entry.Value))
            .Select(entry => ModOf(entry.Key))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mod => priorities.GetValueOrDefault(mod))
            .ThenBy(mod => mod, StringComparer.Ordinal)
            .ToList();

        return definition.Format.Render(mods);
    }

    /// <summary>The mod a file in the game folder belongs to; the order file itself belongs to none.</summary>
    private string? ModOf(GamePath file)
    {
        if (file == Path || !file.InFolder(ModFolder)) return null;

        return definition.Format.ModOf(file.Path.RelativeTo(ModFolder.Path));
    }

    /// <summary>
    /// Whether the node has a file once this sync is done. The loadout side alone is not that: a contributor whose
    /// only file is merged is deleted from disk, and a generated file enters the tree with no loadout side at all.
    /// </summary>
    private static bool WillBeOnDisk(SyncNode node)
        => node.Actions.HasFlag(Actions.ExtractToDisk)
           || node.Actions.HasFlag(Actions.WriteIntrinsic)
           || (node.HaveDisk && !node.Actions.HasFlag(Actions.DeleteFromDisk));
}
