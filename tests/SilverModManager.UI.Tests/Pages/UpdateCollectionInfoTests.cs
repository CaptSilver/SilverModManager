using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The loadout page records the latest revision Nexus has published for an uploaded collection on its group, so the
/// collection update offer can see it.
/// </summary>
public class UpdateCollectionInfoTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<ManagedCollectionLoadoutGroup.ReadOnly> UploadedGroup(RevisionNumber? lastPublished = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Uploaded {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Mine", 1);

        using var tx = Connection.BeginTransaction();
        var group = new ManagedCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = revision.CollectionId,
            CurrentRevisionNumber = RevisionNumber.From(1),
            LastUploadDate = DateTimeOffset.UtcNow,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Mine (group)", LoadoutId = loadout.LoadoutId },
                },
            },
        };
        if (lastPublished is { } published)
            tx.Add(id, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, published);
        var result = await tx.Commit();
        return result.Remap(group);
    }

    private static ValueTask<GraphQlResult<Optional<RevisionNumber>, NotFound>> Published(CollectionMetadata.ReadOnly _, CancellationToken __)
        => new(new GraphQlResult<Optional<RevisionNumber>, NotFound>(Optional<RevisionNumber>.Create(RevisionNumber.From(5))));

    private static ValueTask<GraphQlResult<Optional<RevisionNumber>, NotFound>> NothingPublished(CollectionMetadata.ReadOnly _, CancellationToken __)
        => new(new GraphQlResult<Optional<RevisionNumber>, NotFound>(Optional<RevisionNumber>.None));

    [Fact]
    public async Task StoresTheLatestPublishedRevisionOnTheGroup()
    {
        var group = await UploadedGroup();

        await LoadoutViewModel.UpdateCollectionInfo(group, Connection, Published, CancellationToken.None);

        ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id).LastPublishedRevisionNumber
            .Should().Be(Optional<RevisionNumber>.Create(RevisionNumber.From(5)));
    }

    [Fact]
    public async Task LeavesTheGroupAloneWhenNothingIsPublished()
    {
        var group = await UploadedGroup(RevisionNumber.From(3));

        await LoadoutViewModel.UpdateCollectionInfo(group, Connection, NothingPublished, CancellationToken.None);

        ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id).LastPublishedRevisionNumber
            .Should().Be(Optional<RevisionNumber>.Create(RevisionNumber.From(3)));
    }
}
