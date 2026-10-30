using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using NexusMods.Paths;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Cli;

public static class Verbs
{
    /// <summary>The <c>wabbajack</c> verbs; they need nothing else from the module.</summary>
    public static IServiceCollection AddWabbajackVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("wabbajack", "Commands for Wabbajack modlists")
            .AddVerb(() => Inspect)
            .AddVerb(() => List)
            .AddVerb(() => Download)
            .AddVerb(() => Status)
            .AddVerb(() => Cancel);

    // Reads the zip directly: looking at a list must not add it to the library.
    [Verb("wabbajack inspect", "Prints what a .wabbajack file contains: its details, directive and source counts, and its archives")]
    private static async Task<int> Inspect([Injected] IRenderer renderer,
        [Option("f", "file", "The .wabbajack file")] AbsolutePath file)
    {
        if (!file.FileExists)
        {
            await renderer.TextLine("{0} does not exist", file.ToString());
            return 1;
        }

        ModList list;
        try
        {
            using var zip = ZipFile.OpenRead(file.ToString());
            var entry = zip.Entries.FirstOrDefault(e => ModlistEntryNames.Contains(e.FullName, StringComparer.OrdinalIgnoreCase));
            if (entry is null)
            {
                await renderer.TextLine("{0} has no 'modlist' entry, so it is not a Wabbajack modlist", file.ToString());
                return 1;
            }
            await using var stream = entry.Open();
            list = ModListReader.Read(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedModlistException)
        {
            await renderer.TextLine("{0} cannot be read as a modlist: {1}", file.ToString(), ex.Message);
            return 1;
        }

        await renderer.TextLine("Name: {0}", list.Name);
        await renderer.TextLine("Author: {0}", list.Author);
        await renderer.TextLine("Version: {0}", list.Version);
        await renderer.TextLine("GameType: {0}", list.GameType);
        await renderer.TextLine("Archives: {0}, directives: {1}", list.Archives.Count, list.Directives.Count);

        await renderer.Table(["Directive", "Count"], ModlistInspection.DirectiveKinds(list).Select(k => new object[] { k.Kind, k.Count }));
        await renderer.Table(["Source", "Count"], ModlistInspection.SourceKinds(list).Select(k => new object[] { k.Kind, k.Count }));
        await renderer.Table(["Archive", "Size", "Source", "Detail"], list.Archives.Select(ModlistInspection.ArchiveRow));
        return 0;
    }

    [Verb("wabbajack list", "Lists the modlists in the library with their ids, and marks the one whose archives are downloading")]
    private static async Task<int> List([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IArchiveAcquirer acquirer)
    {
        var running = acquirer.Status is { IsRunning: true } status ? status.Modlist : null;
        await renderer.Table(["Id", "Name", "Version", "Game", "Archives", "Batch"],
            catalog.List().Select(m => (object[])BatchStatusText.ListRow(m, m.Id == running)));
        return 0;
    }

    [Verb("wabbajack download", "Starts downloading a modlist's archives in the running app and returns at once. Progress shows on the modlist's page and in 'wabbajack status'")]
    private static async Task<int> Download([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] ModlistDownloads downloads,
        [Injected] CancellationToken token,
        [Option("m", "modlist", "The modlist: its name, its id from 'wabbajack list', or the file name of its .wabbajack in the library")] string modlist)
    {
        var start = await downloads.Start(catalog.Find(modlist), token);
        await renderer.TextLine("{0}", BatchStatusText.Started(start));
        return 0;
    }

    [Verb("wabbajack status", "Shows whether a modlist download is running: archives done, bytes fetched, what waits on you, and the latest failures")]
    private static async Task<int> Status([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IArchiveAcquirer acquirer,
        [Option("m", "modlist", "Only report this modlist (name, id or file name); without it, whichever batch ran last", true)] string? modlist)
    {
        var wanted = modlist is null ? null : catalog.Find(modlist);
        if (acquirer.Status is not { } status || (wanted is not null && status.Modlist != wanted.Id))
        {
            await renderer.TextLine("{0}", wanted is null
                ? "No download batch has run since the app started."
                : $"No download batch has run for '{wanted.Name}' since the app started.");
            return 0;
        }

        var name = catalog.List().FirstOrDefault(m => m.Id == status.Modlist)?.Name ?? "a modlist";
        var plan = acquirer.Plan.Modlist == status.Modlist ? acquirer.Plan : new ArchivePlan([]);
        foreach (var line in BatchStatusText.Describe(status, name, plan)) await renderer.TextLine("{0}", line);
        return 0;
    }

    [Verb("wabbajack cancel", "Stops the modlist download that is running, and does not resume it after a restart")]
    private static async Task<int> Cancel([Injected] IRenderer renderer, [Injected] IArchiveAcquirer acquirer, [Injected] ModlistDownloads downloads)
    {
        if (acquirer.Status is not { IsRunning: true })
        {
            await renderer.TextLine("No modlist download is running.");
            return 0;
        }

        downloads.Cancel();
        await renderer.TextLine("Stopping the running modlist download.");
        return 0;
    }

    private static readonly string[] ModlistEntryNames = ["modlist", "modlist.json"];
}
