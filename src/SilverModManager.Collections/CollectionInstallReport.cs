using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Collections;

/// <summary>
/// Runs a collection install for the command line and says what did not install: the mods that failed, and the mods that
/// wait for a person to finish them in the app.
/// </summary>
public static class CollectionInstallReport
{
    /// <summary>
    /// Installs the required items of <paramref name="revision"/> into <paramref name="loadout"/> without opening any installer
    /// window, prints a "Not installed" table for the failures and a "Need review" table for the mods that need a person.
    /// </summary>
    /// <returns>1 when a mod failed to install, 0 otherwise. Mods that only need review don't fail the run.</returns>
    public static async Task<int> Run(
        IRenderer renderer,
        IServiceProvider serviceProvider,
        LoadoutId loadout,
        NexusModsCollectionLibraryFile.ReadOnly file,
        CollectionRevisionMetadata.ReadOnly revision,
        CancellationToken cancellationToken)
    {
        var items = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required);
        var job = InstallCollectionJob.Create(serviceProvider, loadout, file, revision, items);
        // The job is awaitable but not a Task; wrapping it lets the caller's token stop the wait.
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Install() => await job;
        await Install().WaitAsync(cancellationToken);

        var definition = job.JobDefinition;

        if (definition.FailedItems.Count > 0)
        {
            await renderer.TextLine($"Not installed ({definition.FailedItems.Count}):");
            await renderer.Table(["Name", "Reason"], definition.FailedItems.Select(item => new object[] { item.Name, CliErrors.Describe(item.Exception) }));
        }

        if (definition.PendingItems.Count > 0)
        {
            await renderer.TextLine($"Need review ({definition.PendingItems.Count}):");
            await renderer.Table(["Name", "Kind", "Reason"], definition.PendingItems.Select(item => new object[] { item.Name, item.Kind.ToString(), item.Reason }));
            await renderer.TextLine("Open the collection in the app and use Review to finish them.");
        }

        return definition.FailedItems.Count > 0 ? 1 : 0;
    }
}
