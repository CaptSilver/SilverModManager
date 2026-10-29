using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Collections;
using SilverModManager.Games.FOMOD;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary><c>collection reapply-order</c>: it answers on the command line and never reaches for a dialog.</summary>
[Collection("ConsoleOutput")]
public class CollectionReapplyOrderVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<CollectionReapplyOrderVerbTests, ThunderstoreStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        return services;
    }

    [Fact]
    public async Task ACollectionTheLoadoutDoesNotHaveIsReportedAndExitsOne()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, "collection", "reapply-order", "--loadout", loadout.Name, "--collection", "no-such-collection");

        code.Should().Be(1, output);
        output.Should().Contain("no collection `no-such-collection`");
    }

    [Fact]
    public async Task TheVerbIsListedUnderCollection()
    {
        var (_, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, "collection", "reapply-order", "--help");

        output.Should().Contain("reapply-order");
    }

    /// <summary>A collection of the offline helper installed into the loadout, with no mods: enough for the verb to find and re-apply.</summary>
    private async Task<Loadout.ReadOnly> LoadoutWithACollection()
    {
        var (file, revision, _) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager).Create([]);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = revision.Collection,
            RevisionId = revision,
            LibraryFileId = file,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = revision.Collection.Name, LoadoutId = loadout.LoadoutId, IsDisabled = true },
                },
            },
        };
        await tx.Commit();
        return loadout;
    }

    [Fact]
    public async Task ARefusalWhileTheGameRunsIsAPlainSentenceAndExitsOne()
    {
        var loadout = await LoadoutWithACollection();
        ServiceProvider.GetRequiredService<FakeGameLaunchSession>().Running = true;

        // The refusal is printed through "{0}", which the shared Run helper reads back unformatted.
        var renderer = new LoggingRenderer();
        var code = await ServiceProvider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["collection", "reapply-order", "--loadout", loadout.Name, "--collection", "offline"], renderer, CancellationToken.None);
        var output = string.Concat(renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments ?? [])));

        code.Should().Be(1, output);
        output.Should().Contain("is running from SilverModManager").And.Contain("apply the load order again").And.NotContain("Details are in the log").And.NotContain(" at SilverModManager");
    }
}

/// <summary>
/// <c>collection reapply-order</c> on a game that keeps a load order: the verb puts a scrambled collection back in
/// the order its manifest lists.
/// </summary>
[Collection("ConsoleOutput")]
public class CollectionReapplyOrderVerbOrderTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionReapplyOrderVerbOrderTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services).AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        return services;
    }

    // Winner first, as the manifest writes it: the last mod listed is the first one loaded.
    private static SilverLoadOrderEntry[] WinnerFirst(IReadOnlyList<Mod> mods) =>
        [..new[] { 2, 0, 1 }.Select(i => new SilverLoadOrderEntry { Md5 = mods[i].Source.Md5, Name = mods[i].Name })];

    private string[] Names(NexusCollectionLoadoutGroup.ReadOnly group) =>
        ContainerOrder.ChildrenWinnerFirst(Connection.Db, group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId)
            .Select(child => child.AsLoadoutItem().Name.Replace(".zip", "").Replace("mod", "Plain mod ")).ToArray();

    [Fact]
    public async Task AReapplyPutsAScrambledCollectionBackInItsOrderAndExitsZero()
    {
        var mods = Enumerable.Range(0, 3).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager)
            .Create(mods, silverLoadOrder: WinnerFirst);
        var loadout = await CreateLoadout();
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        var group = await Run().WaitAsync(TimeSpan.FromSeconds(60));
        var expected = Names(group);
        expected.Select(name => name.Last().ToString()).Should().Equal("2", "0", "1");

        // What an older matcher left behind: the same mods, in the opposite order.
        var container = group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;
        var reversed = ContainerOrder.ChildrenWinnerFirst(Connection.Db, container).Reverse().Select(child => new ModOrderEntry(child.LoadoutItemGroupId, null));
        using (var tx = Connection.BeginTransaction())
        {
            new ModOrderImporter(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance)
                .Import(Loadout.Load(Connection.Db, loadout.LoadoutId), ModOrder.Create(reversed, []), new ModOrderSource($"collection {group.Collection.Name}", container, Manifest: null), tx);
            await tx.Commit();
        }
        Names(group).Should().Equal(expected.Reverse(), "the scramble put the mods in the wrong order");

        var (code, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, "collection", "reapply-order", "--loadout", loadout.Name, "--collection", "offline");

        code.Should().Be(0, output);
        output.Should().Contain("Offline: the load order was applied again.");
        Names(group).Should().Equal(expected, "the manifest lists the winner first, so the winner sits last in the load order");
    }
}
