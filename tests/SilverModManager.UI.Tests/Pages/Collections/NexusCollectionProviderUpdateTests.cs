using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages.Collections;

/// <summary>
/// An installed Nexus collection is offered an update once the latest published revision recorded on its group is
/// newer than the revision it is on; the offer targets that revision without asking Nexus anything.
/// </summary>
public class NexusCollectionProviderUpdateTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private async Task<NexusCollectionLoadoutGroup.ReadOnly> InstalledOnFirstRevision()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Nexus update {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Quality of Life");
        return await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Quality of Life (group)", revision);
    }

    private async Task RecordLastPublished(NexusCollectionLoadoutGroup.ReadOnly group, ulong number)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Id, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, RevisionNumber.From(number));
        await tx.Commit();
    }

    private IObservable<ICollectionUpdate?> Offer(NexusCollectionLoadoutGroup.ReadOnly group) =>
        new NexusCollectionProvider(Provider).ObserveUpdate(CollectionGroup.Load(Connection.Db, group.Id));

    [Fact]
    public async Task ANewerLastPublishedRevisionNumberYieldsAnUpdateOffer()
    {
        var group = await InstalledOnFirstRevision();
        (await Offer(group).FirstAsync()).Should().BeNull("nothing newer is known yet");

        await RecordLastPublished(group, 3);

        var update = await Offer(group).FirstAsync();
        update.Should().NotBeNull();
        update!.Name.Should().Be("Quality of Life");
        update.CurrentVersionText.Should().Be("Revision 1");
        update.NewVersionText.Should().Be("Revision 3");
    }

    [Fact]
    public async Task AnOfferAppearsWhileTheGroupIsObserved()
    {
        var group = await InstalledOnFirstRevision();
        var offers = new TaskCompletionSource<ICollectionUpdate>();
        using var subscription = Offer(group).Where(update => update is not null).Subscribe(update => offers.TrySetResult(update!));

        await RecordLastPublished(group, 2);

        var update = await offers.Task.WaitAsync(Wait);
        update.NewVersionText.Should().Be("Revision 2");
    }

    [Fact]
    public async Task NoOfferIsMadeForARevisionThatIsNotNewer()
    {
        var group = await InstalledOnFirstRevision();

        await RecordLastPublished(group, 1);

        (await Offer(group).FirstAsync()).Should().BeNull();
    }

    [Fact]
    public async Task OnlyAChangeOfTheInstalledOrTheTargetRevisionRepeatsTheOffer()
    {
        var group = await InstalledOnFirstRevision();
        await RecordLastPublished(group, 3);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var subscription = Offer(group).Subscribe(update => seen.Enqueue(update is null ? null : $"{update.CurrentVersionText} > {update.NewVersionText}"));

        await RecordLastPublished(group, 3);
        await InstallRevision(group, 2);
        await RecordLastPublished(group, 4);

        await Task.Run(async () =>
        {
            while (seen.Count < 3) await Task.Delay(20);
        }).WaitAsync(Wait);
        seen.Should().Equal("Revision 1 > Revision 3", "Revision 2 > Revision 3", "Revision 2 > Revision 4");
    }

    [Fact]
    public async Task TheOfferGoesBackToNullOnceTheInstalledRevisionReachesThePublishedOne()
    {
        var group = await InstalledOnFirstRevision();
        await RecordLastPublished(group, 2);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var subscription = Offer(group).Subscribe(update => seen.Enqueue(update?.NewVersionText));

        await InstallRevision(group, 2);

        await Task.Run(async () =>
        {
            while (seen.Count < 2) await Task.Delay(20);
        }).WaitAsync(Wait);
        seen.Should().Equal("Revision 2", null);
    }

    [Fact]
    public async Task RecordingThePublishedRevisionOfAnInstalledGroupMakesTheOffer()
    {
        var group = await InstalledOnFirstRevision();

        await CollectionUpdater.RecordLatestPublishedRevision(Connection, group, RevisionNumber.From(3));

        (await Offer(group).FirstAsync())!.NewVersionText.Should().Be("Revision 3");
    }

    [Fact]
    public async Task RecordingTheRevisionAlreadyRecordedWritesNothing()
    {
        var group = await InstalledOnFirstRevision();
        await CollectionUpdater.RecordLatestPublishedRevision(Connection, group, RevisionNumber.From(3));
        var basis = Connection.Db.BasisTxId;

        await CollectionUpdater.RecordLatestPublishedRevision(Connection, group, RevisionNumber.From(3));

        Connection.Db.BasisTxId.Should().Be(basis);
    }

    /// <summary>The group moves to another revision of its collection that the library holds, as an update does.</summary>
    private async Task InstallRevision(NexusCollectionLoadoutGroup.ReadOnly group, ulong number)
    {
        var installed = NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id);
        var revision = await CollectionSeed.AddRevision(Connection, installed.Collection.GameId, installed.Collection.Name, number, existing: installed.Collection);
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Id, NexusCollectionLoadoutGroup.Revision, revision);
        await tx.Commit();
    }
}
