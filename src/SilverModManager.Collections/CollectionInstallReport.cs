using System.Text.Json;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
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
    /// <param name="json">Print one JSON object (installed, pending, missingLinks, failed) instead of the tables.</param>
    /// <returns>1 when a mod failed to install, 0 otherwise. Mods that only need review don't fail the run.</returns>
    public static async Task<int> Run(
        IRenderer renderer,
        IServiceProvider serviceProvider,
        LoadoutId loadout,
        NexusModsCollectionLibraryFile.ReadOnly file,
        CollectionRevisionMetadata.ReadOnly revision,
        bool json,
        CancellationToken cancellationToken)
    {
        var items = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required);
        var job = InstallCollectionJob.Create(serviceProvider, loadout, file, revision, items);
        // The job is awaitable but not a Task; wrapping it lets the caller's token stop the wait.
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Install() => await job;
        await Install().WaitAsync(cancellationToken);

        var definition = job.JobDefinition;
        var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        if (json)
        {
            var failed = definition.FailedItems.Select(item => new FailedRow(item.Name, CliErrors.Describe(item.Exception, fileSystem))).ToArray();
            var pending = definition.PendingItems.Select(item => new PendingRow(item.Name, item.Kind.ToString(), item.Reason)).ToArray();
            var notInstalled = definition.FailedItems.Select(item => item.Download.Id).Concat(definition.PendingItems.Select(item => item.Download.Id)).ToHashSet();
            var installed = items.Where(item => !notInstalled.Contains(item.Id)).Select(item => item.Name).ToArray();
            await renderer.TextLine(JsonSerializer.Serialize(new Report(installed, pending, [], failed), CliJson.Options));
            return failed.Length > 0 ? 1 : 0;
        }

        if (definition.FailedItems.Count > 0)
        {
            await renderer.TextLine($"Not installed ({definition.FailedItems.Count}):");
            await renderer.Table(["Name", "Reason"], definition.FailedItems.Select(item => new object[] { item.Name, CliErrors.Describe(item.Exception, fileSystem) }));
        }

        if (definition.PendingItems.Count > 0)
        {
            await renderer.TextLine($"Need review ({definition.PendingItems.Count}):");
            await renderer.Table(["Name", "Kind", "Reason"], definition.PendingItems.Select(item => new object[] { item.Name, item.Kind.ToString(), item.Reason }));
            await renderer.TextLine("Open the collection in the app and use Review to finish them.");
        }

        return definition.FailedItems.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Whether the account may use the download API. Asks Nexus Mods rather than reading the login manager's cached flag,
    /// which a command-line run has not filled in yet.
    /// </summary>
    /// <exception cref="LoadoutVerbRefusedException">Nexus Mods did not answer. An outage is not read as a free account,
    /// which would hand a premium user a list of browser links.</exception>
    public static async Task<bool> IsPremium(ILoginManager loginManager, CancellationToken cancellationToken)
    {
        try
        {
            return await NexusAccount.Current(loginManager, cancellationToken) == NexusAccountKind.Premium;
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new LoadoutVerbRefusedException($"Nexus Mods did not answer: {e.Message}");
        }
    }

    /// <summary>
    /// Prints the downloads a free account has to start in the browser and returns 2: nothing was installed, and a script
    /// must be able to tell that from a finished run.
    /// </summary>
    public static async Task<int> ReportMissingLinks(IRenderer renderer, IReadOnlyList<(CollectionDownload.ReadOnly Download, Uri Uri)> links, bool json)
    {
        if (json)
        {
            var missing = links.Select(link => new LinkRow(link.Download.Name, link.Uri.ToString())).ToArray();
            await renderer.TextLine(JsonSerializer.Serialize(new Report([], [], missing, []), CliJson.Options));
        }
        else
        {
            await renderer.TextLine($"Missing {links.Count} downloads:");
            await renderer.Table(["Name", "Uri"], links.Select(link => new object[] { link.Download.Name, link.Uri.ToString() }));
        }
        return 2;
    }

    private sealed record Report(string[] Installed, PendingRow[] Pending, LinkRow[] MissingLinks, FailedRow[] Failed);
    private sealed record PendingRow(string Name, string Kind, string Reason);
    private sealed record LinkRow(string Name, string Uri);
    private sealed record FailedRow(string Name, string Reason);
}
