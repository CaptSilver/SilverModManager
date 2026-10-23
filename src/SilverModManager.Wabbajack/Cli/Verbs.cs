using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using NexusMods.Paths;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Cli;

public static class Verbs
{
    /// <summary>The <c>wabbajack</c> verbs; they need nothing else from the module.</summary>
    public static IServiceCollection AddWabbajackVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("wabbajack", "Commands for Wabbajack modlists")
            .AddVerb(() => Inspect);

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

    private static readonly string[] ModlistEntryNames = ["modlist", "modlist.json"];
}
