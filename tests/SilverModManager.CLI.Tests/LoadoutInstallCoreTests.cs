using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The install step every mod source shares: download first, one restore point, then install or enable. The library
/// items are the two packages a Thunderstore install brings, taken out of the loadout again so the core has real
/// archives to install.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutInstallCoreTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutInstallCoreTests, ThunderstoreStubbedGame>(helper)
{
    private static readonly ThunderstoreVersionRef RepoLib = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0));

    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        return services;
    }

    private ILoadoutInstallCore Core => ServiceProvider.GetRequiredService<ILoadoutInstallCore>();
    private int Revisions(Loadout.ReadOnly loadout) => ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Id).Count;

    private int Links(Loadout.ReadOnly loadout)
        => LibraryLinkedLoadoutItem.All(Connection.Db).Count(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId);

    // Two library items nothing links: install them, then drop their groups.
    private async Task<LibraryItem.ReadOnly[]> UnlinkedItems(Loadout.ReadOnly loadout)
    {
        var installer = ServiceProvider.GetRequiredService<IThunderstoreInstaller>();
        var outcomes = await installer.InstallAsync(RepoLib, loadout.LoadoutId, CancellationToken.None);
        var groups = LibraryLinkedLoadoutItem.All(Connection.Db)
            .Where(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId)
            .Select(link => link.AsLoadoutItemGroup().LoadoutItemGroupId)
            .ToArray();
        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveItems(groups);
        return outcomes.Select(o => LibraryItem.Load(Connection.Db, o.Item.Id)).ToArray();
    }

    [Fact]
    public async Task Run_DownloadsEverythingBeforeTheRestorePoint()
    {
        var loadout = await CreateLoadout();
        var items = await UnlinkedItems(loadout);
        var before = Revisions(loadout);
        var linksBefore = Links(loadout);
        var revisionsDuringDownloads = new List<int>();
        var linksDuringDownloads = new List<int>();

        Task<LibraryItem.ReadOnly> Download(LibraryItem.ReadOnly item)
        {
            revisionsDuringDownloads.Add(Revisions(loadout));
            linksDuringDownloads.Add(Links(loadout));
            return Task.FromResult(item);
        }

        var outcomes = await Core.Run(loadout.LoadoutId, "key",
            items.Select(item => new ResolvedInstall(item.Name, false, null, _ => Download(item))).ToArray(),
            CancellationToken.None);

        revisionsDuringDownloads.Should().Equal(before, before);
        linksDuringDownloads.Should().Equal(linksBefore, linksBefore);
        outcomes.Should().OnlyContain(o => o.Downloaded && o.Outcome == LoadoutPackageOutcome.Installed);
        var revisions = ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Id);
        revisions.Count.Should().Be(before + 1);
        revisions[^1].ModCount.Should().Be(linksBefore, "the restore point is written before the first link");
        Links(loadout).Should().Be(linksBefore + 2);
    }

    [Fact]
    public async Task Run_WritesNoMarkerWhenADownloadFails()
    {
        var loadout = await CreateLoadout();
        var items = await UnlinkedItems(loadout);
        var before = Revisions(loadout);

        var act = () => Core.Run(loadout.LoadoutId, "key",
        [
            new ResolvedInstall(items[0].Name, false, null, _ => Task.FromResult(items[0])),
            new ResolvedInstall(items[1].Name, false, null, _ => throw new IOException("disk gone")),
        ], CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        Revisions(loadout).Should().Be(before);
        Links(loadout).Should().Be(0);
    }

    [Fact]
    public async Task Run_EnablesADisabledCopy()
    {
        var loadout = await CreateLoadout();
        var items = await UnlinkedItems(loadout);
        await Core.Run(loadout.LoadoutId, "first", [new ResolvedInstall(items[0].Name, false, items[0], _ => throw new InvalidOperationException())], CancellationToken.None);
        var link = LibraryLinkedLoadoutItem.All(Connection.Db).Single(l => l.AsLoadoutItem().LoadoutId == loadout.LoadoutId);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(link.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var outcomes = await Core.Run(loadout.LoadoutId, "second", [new ResolvedInstall(items[0].Name, false, items[0], _ => throw new InvalidOperationException())], CancellationToken.None);

        outcomes.Should().ContainSingle().Which.Should().Match<InstallOutcome>(o => o.Outcome == LoadoutPackageOutcome.Enabled && !o.Downloaded);
    }

    [Fact]
    public async Task Run_SecondCallerJoinsTheFirstRun()
    {
        var loadout = await CreateLoadout();
        var items = await UnlinkedItems(loadout);
        var gate = new TaskCompletionSource<LibraryItem.ReadOnly>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        ResolvedInstall[] Resolved() => [new ResolvedInstall(items[0].Name, false, null, _ =>
        {
            Interlocked.Increment(ref downloads);
            return gate.Task;
        })];

        var first = Core.Run(loadout.LoadoutId, "same", Resolved(), CancellationToken.None);
        var second = Core.Run(loadout.LoadoutId, "same", Resolved(), CancellationToken.None);
        gate.SetResult(items[0]);
        var results = await Task.WhenAll(first, second);

        downloads.Should().Be(1);
        results[1].Should().Equal(results[0]);
        Links(loadout).Should().Be(1);
    }
}
