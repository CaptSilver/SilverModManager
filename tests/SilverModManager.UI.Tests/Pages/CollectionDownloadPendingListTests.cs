using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The collection page's review list follows the pending rows of its group, also across a deactivation.</summary>
public class CollectionDownloadPendingListTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Seeded(CollectionDownloadViewModel ViewModel, EntityId Group, CollectionDownload.ReadOnly[] Downloads);

    private async Task<Seeded> SeedPage(int rows)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Pending list loadout {Guid.NewGuid():N}");
        var revision = await CollectionSeed.AddRevision(Connection, Cyberpunk2077Game.NexusModsGameId.Value, "Pending list pack");

        using var tx = Connection.BeginTransaction();
        var created = Enumerable.Range(0, rows)
            .Select(i => new CollectionDownload.New(tx) { Name = $"Pending mod {i}", IsOptional = i % 2 == 1, ArrayIndex = i, CollectionRevisionId = revision.Id })
            .ToArray();
        var result = await tx.Commit();
        var downloads = created.Select(download => CollectionDownload.Load(result.Db, result[download.Id])).ToArray();

        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Pending list pack", revision);
        foreach (var download in downloads)
            await PendingInstalls.Record(Connection, group.Id, download, ReviewKind.NoInstaller, "no match");

        var viewModel = new CollectionDownloadViewModel(new FakeWindowManager(), Provider, revision, loadout.LoadoutId);
        return new Seeded(viewModel, group.Id, downloads);
    }

    private async Task Retract(Seeded seeded, CollectionDownload.ReadOnly download)
    {
        using var tx = Connection.BeginTransaction();
        PendingInstalls.Retract(tx, Connection.Db, seeded.Group, CollectionDownloadId.From(download.Id));
        await tx.Commit();
    }

    private static async Task<string[]> ListedNames(CollectionDownloadViewModel viewModel) =>
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.PendingItems.Select(static row => row.Name).Order().ToArray());

    private static async Task<int> Count(CollectionDownloadViewModel viewModel) =>
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.PendingCount.Value);

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(25);
        }
    }

    private static async Task ShouldList(CollectionDownloadViewModel viewModel, params string[] names)
    {
        await Eventually(async () => (await ListedNames(viewModel)).SequenceEqual(names.Order()) && await Count(viewModel) == names.Length);
        (await ListedNames(viewModel)).Should().Equal(names.Order());
        (await Count(viewModel)).Should().Be(names.Length);
    }

    [Fact]
    public async Task TheListFollowsRowsThatAreRetractedAndDismissed()
    {
        var seeded = await SeedPage(3);
        using var activation = seeded.ViewModel.Activator.Activate();
        await ShouldList(seeded.ViewModel, "Pending mod 0", "Pending mod 1", "Pending mod 2");

        await Retract(seeded, seeded.Downloads[0]);
        await ShouldList(seeded.ViewModel, "Pending mod 1", "Pending mod 2");

        var dismissed = PendingInstalls.Find(Connection.Db, seeded.Group, CollectionDownloadId.From(seeded.Downloads[1].Id)).Value;
        await PendingInstalls.Dismiss(Connection, dismissed.Id);
        await ShouldList(seeded.ViewModel, "Pending mod 2");
    }

    [Fact]
    public async Task ARowRetractedWhileDeactivatedIsGoneOnceTheViewModelIsActiveAgain()
    {
        var seeded = await SeedPage(3);
        var first = seeded.ViewModel.Activator.Activate();
        await ShouldList(seeded.ViewModel, "Pending mod 0", "Pending mod 1", "Pending mod 2");

        first.Dispose();
        await Retract(seeded, seeded.Downloads[1]);
        using var second = seeded.ViewModel.Activator.Activate();

        await ShouldList(seeded.ViewModel, "Pending mod 0", "Pending mod 2");
        var count = await Count(seeded.ViewModel);
        (await Dispatcher.UIThread.InvokeAsync(() => seeded.ViewModel.PendingItems.Count)).Should().Be(count);
    }
}
