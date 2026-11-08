using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Pages.Collections.Update;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Collections.Tests;

using Revision = (NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items);

/// <summary>
/// The Nexus side of the shared update flow: <see cref="NexusCollectionUpdate"/> reviews and applies through
/// <see cref="CollectionUpdater"/>. Revision 1 lists u1 and u2; revision 2 lists them and a new required mod, nw.
/// </summary>
public class CollectionUpdateNexusAdapterTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionUpdateNexusAdapterTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        // Not logged in, so a revision the library lacks cannot be fetched and no login window opens.
        var login = Substitute.For<ILoginManager>();
        login.EnsureLoggedIn(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return base.AddServices(services)
            .AddSingleton(login)
            .AddSingleton(Substitute.For<IWindowManager>())
            .AddTransient<IGuidedInstaller>(_ => new ScriptedGuidedInstallerState().Create());
    }

    private async Task<Mod> NexusMod(string slot, uint modId, uint fileId, int archive, bool inLibrary = true)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, _) = await Offline.CreateMod(folder.Path, archive, ModShape.Plain);
        if (inLibrary) await Offline.AddNexusDownload(path, modId, fileId);
        return new Mod
        {
            Name = $"Mod {slot}",
            DomainName = GameDomain.From("cyberpunk2077"),
            Source = new ModSource
            {
                Type = ModSourceType.NexusMods,
                ModId = ModId.From(modId),
                FileId = FileId.From(fileId),
                LogicalFilename = $"{slot}-{fileId}.zip",
                FileSize = Size.From((ulong)path.FileInfo.Size),
                Tag = slot,
            },
        };
    }

    private async Task<Revision> Publish(int revisionNumber, Mod[] mods)
    {
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods,
            ModRules = [],
        };
        var (file, revision, _) = await Offline.Create(manifest, [], revisionNumber);
        return (file, revision, CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required));
    }

    private sealed record Scenario(NexusCollectionLoadoutGroup.ReadOnly Group, NexusCollectionUpdate Update);

    /// <summary>Revision 1 installed, revision 2 in the library. With <paramref name="newModInLibrary"/> false the archive of nw is not there.</summary>
    private async Task<Scenario> InstalledFirstRevision(bool newModInLibrary = true)
    {
        var u1 = await NexusMod("u1", 100, 1001, 0);
        var u2 = await NexusMod("u2", 101, 1002, 1);
        var first = await Publish(1, [u1, u2]);
        await Publish(2, [u1, u2, await NexusMod("nw", 107, 1008, 8, newModInLibrary)]);

        var loadout = await CreateLoadout();
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Install() => await InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items);
        var group = await Install().WaitAsync(Timeout);
        return new Scenario(group, new NexusCollectionUpdate(ServiceProvider, group, RevisionNumber.From(2)));
    }

    private RevisionNumber InstalledRevision(NexusCollectionLoadoutGroup.ReadOnly group) => NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).Revision.RevisionNumber;

    [Fact]
    public async Task TheNexusAdapterReviewsWithoutWriting()
    {
        var s = await InstalledFirstRevision();
        var basis = Connection.Db.BasisTxId;

        var review = await s.Update.Review(null, CancellationToken.None);

        Connection.Db.BasisTxId.Should().Be(basis, "a review changes nothing");
        review.Added.Should().Equal("Mod nw");
        review.Changed.Should().BeEmpty();
        review.Dropped.Should().BeEmpty();
        review.Downloads.Should().BeEmpty("every archive is in the library");
        review.WritesRestorePoint.Should().BeFalse();
        review.DroppedAreRemovedNotAsked.Should().BeFalse();
        (await s.Update.IsDownloaded(CancellationToken.None)).Should().BeTrue();
        InstalledRevision(s.Group).Value.Should().Be(1UL);
    }

    [Fact]
    public async Task TheReviewListsTheArchivesTheLibraryLacks()
    {
        var s = await InstalledFirstRevision(newModInLibrary: false);

        var review = await s.Update.Review(null, CancellationToken.None);

        review.Downloads.Select(archive => archive.Name).Should().Equal("Mod nw");
        review.Downloads.Single().Source.Should().Be("Nexus Mods");
        (await s.Update.IsDownloaded(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task TheNamesTheFlowShowsAreTheCollectionAndBothRevisions()
    {
        var s = await InstalledFirstRevision();

        s.Update.Name.Should().Be("Offline");
        s.Update.CurrentVersionText.Should().Be("Revision 1");
        s.Update.NewVersionText.Should().Be("Revision 2");
        s.Update.Note.Should().BeNull();
    }

    [Fact]
    public async Task TheNexusAdapterAppliesThroughCollectionUpdater()
    {
        var s = await InstalledFirstRevision();

        var outcome = await s.Update.Apply(skipMissing: false, CancellationToken.None);

        outcome.Updated.Should().BeTrue();
        outcome.Error.Should().BeNull();
        outcome.Failed.Should().Be(0);
        outcome.ToastText.Should().Be("Collection updated");
        outcome.RestorePoint.Should().BeNull("a Nexus update writes no restore point");
        outcome.Undo.Should().BeNull("with no restore point there is nothing to undo");
        InstalledRevision(s.Group).Value.Should().Be(2UL);
    }

    [Fact]
    public async Task ARefusedApplyReportsTheReasonInsteadOfThrowing()
    {
        var s = await InstalledFirstRevision();
        (await s.Update.Apply(skipMissing: false, CancellationToken.None)).Updated.Should().BeTrue();

        var outcome = await s.Update.Apply(skipMissing: false, CancellationToken.None);

        outcome.Updated.Should().BeFalse();
        outcome.Error.Should().NotBeNull();
        outcome.ToastText.Should().Be("Couldn't update to revision 2: The collection is already on that revision.");
    }

    [Fact]
    public async Task ARefusedApplyStillHandsOverWhatTheRepairDropped()
    {
        var u1 = await NexusMod("u1", 100, 1001, 0);
        var u2 = await NexusMod("u2", 101, 1002, 1);
        var first = await Publish(1, [u1, u2, await NexusMod("dr", 106, 1005, 4)]);
        var second = await Publish(2, [u1, u2]);
        var third = await Publish(3, [u1, u2, await NexusMod("missing", 109, 1099, 40, inLibrary: false)]);
        var loadout = await CreateLoadout();
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Install() => await InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items);
        var group = await Install().WaitAsync(Timeout);

        // Switched to revision 2 the way an earlier update did: dr keeps revision 1's download.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group.Id, NexusCollectionLoadoutGroup.Revision, second.Revision);
            tx.Add(group.Id, NexusCollectionLoadoutGroup.LibraryFile, second.File);
            var olds = NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).GetCollectionItems().OrderBy(item => item.Download.ArrayIndex).ToArray();
            var news = second.Revision.Downloads.OrderBy(download => download.ArrayIndex).ToArray();
            for (var i = 0; i < news.Length; i++) tx.Add(olds[i].Id, NexusCollectionItemLoadoutGroup.Download, news[i]);
            await tx.Commit();
        }

        var outcome = await new NexusCollectionUpdate(ServiceProvider, group, RevisionNumber.From(3)).Apply(skipMissing: false, CancellationToken.None);

        outcome.Updated.Should().BeFalse();
        outcome.Error.Should().BeOfType<CollectionUpdateRefusedException>();
        outcome.ToastText.Should().StartWith("Couldn't update to revision 3: ").And.Contain("Mod missing");
        outcome.DroppedToAsk.Should().ContainSingle("the repair moved dr out of the collection before the refusal");
        outcome.DroppedPromptRevision.Should().Be(2, "the prompt names the revision the collection is on");
        _ = third;
    }

    [Fact]
    public async Task AnUpdateToARevisionTheLibraryLacksSaysSoAsARefusal()
    {
        var s = await InstalledFirstRevision();
        var update = new NexusCollectionUpdate(ServiceProvider, s.Group, RevisionNumber.From(9));

        var review = () => update.Review(null, CancellationToken.None);

        var refused = (await review.Should().ThrowAsync<UserFacingException>()).Which;
        refused.Title.Should().NotBeNullOrWhiteSpace();
        refused.Message.Should().Contain("`Revision 9`").And.Contain("`Offline`");
    }

    [Fact]
    public async Task AnAppliedUpdateWithoutSkippedRulesCarriesNoWarnings()
    {
        var s = await InstalledFirstRevision();

        var outcome = await s.Update.Apply(skipMissing: false, CancellationToken.None);

        outcome.Warnings.Should().Be(0);
        outcome.DroppedPromptRevision.Should().Be(2);
    }
}
