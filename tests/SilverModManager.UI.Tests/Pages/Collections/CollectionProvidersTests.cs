using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Reactive.Linq;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.Collections;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages.Collections;

public class CollectionProvidersTests(IServiceProvider provider) : AUiTest(provider)
{
    private CollectionProviders Providers => Provider.GetRequiredService<CollectionProviders>();

    private async Task<(Loadout.ReadOnly Loadout, CollectionRevisionMetadata.ReadOnly Revision)> SeedNexusInstall(string name)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"{name} {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, name);
        return (loadout, revision);
    }

    [Fact]
    public async Task ANexusGroupGetsTheNexusSource()
    {
        var (loadout, revision) = await SeedNexusInstall("Nexus pick");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Nexus pick", revision);

        var source = Providers.For(group.AsCollectionGroup(), loadout.LoadoutId);

        source.Should().BeOfType<NexusCollectionSource>();
    }

    [Fact]
    public async Task AnyOtherGroupFallsBackToTheLocalSource()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Fallback {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Mine");

        var source = Providers.For(group, loadout.LoadoutId);

        source.Should().BeOfType<LocalCollectionSource>();
    }

    [Fact]
    public async Task ALocalGroupIsAlwaysListedAsInstalled()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Listed {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Mine");

        (await Providers.IsListedAsInstalled(group).FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ANexusGroupIsListedOnceEveryRequiredModIsInstalled()
    {
        var (loadout, revision) = await SeedNexusInstall("Nexus listed");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Nexus listed", revision);

        var expected = await Provider.GetRequiredService<CollectionDownloader>()
            .IsListedAsInstalledObservable(group).FirstAsync();

        (await Providers.IsListedAsInstalled(group.AsCollectionGroup()).FirstAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task AnUninstalledRevisionOfTheLoadoutsGameIsPending()
    {
        var (loadout, revision) = await SeedNexusInstall("Pending here");

        var pending = await PendingKeys(loadout, revision.Id);

        pending.Keys.Should().Contain(revision.Id);
    }

    [Fact]
    public async Task AnInstalledRevisionIsNotPending()
    {
        var (loadout, installed) = await SeedNexusInstall("Installed here");
        await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Installed here", installed);
        var offered = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Still offered");

        var pending = await PendingKeys(loadout, offered.Id);

        pending.Keys.Should().NotContain(installed.Id);
    }

    [Fact]
    public async Task ARevisionOfAnotherGameIsNotPending()
    {
        var (loadout, offered) = await SeedNexusInstall("Same game");
        var elsewhere = await CollectionSeed.AddRevision(Connection, NexusModsGameId.From(987_654), "Elsewhere");

        var pending = await PendingKeys(loadout, offered.Id);

        pending.Keys.Should().NotContain(elsewhere.Id);
    }

    [Fact]
    public async Task APendingEntryIsASourceThatOpensTheDownloadPage()
    {
        var (loadout, revision) = await SeedNexusInstall("Pending page");

        var pending = await PendingKeys(loadout, revision.Id);

        pending.Lookup(revision.Id).Value.PendingPage!.FactoryId.Should().Be(CollectionDownloadPageFactory.StaticId);
    }

    [Fact]
    public async Task BuildingSourcesResolvesOnlyWhatReadingTheDatabaseNeeds()
    {
        var counting = new CountingServices(Provider);
        var (loadout, revision) = await SeedNexusInstall("Cheap");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Cheap", revision);
        var offered = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Cheap offered");
        var providers = new CollectionProviders(
            [new NexusCollectionProvider(counting)],
            new LocalCollectionProvider(counting));

        _ = providers.For(group.AsCollectionGroup(), loadout.LoadoutId);
        using var pending = providers.ObservePending(loadout).AsObservableCache();
        await WaitFor(() => pending.Lookup(offered.Id).HasValue);

        counting.Resolved.Should().OnlyContain(
            type => type == typeof(IConnection) || type == typeof(CollectionDownloader),
            "a menu builds one source per collection, so a source must not resolve the download page, the job monitor, a pipeline or the library service");
    }

    /// <summary>The pending sources once <paramref name="expected"/> is among them, so a missing key is a real absence.</summary>
    private async Task<IObservableCache<CollectionSource, EntityId>> PendingKeys(Loadout.ReadOnly loadout, EntityId expected)
    {
        var cache = Providers.ObservePending(loadout).AsObservableCache();
        await WaitFor(() => cache.Lookup(expected).HasValue);
        return cache;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never became true");
            await Task.Delay(20);
        }
    }

    private sealed class CountingServices(IServiceProvider inner) : IServiceProvider
    {
        public List<Type> Resolved { get; } = [];

        public object? GetService(Type serviceType)
        {
            Resolved.Add(serviceType);
            return inner.GetService(serviceType);
        }
    }
}
