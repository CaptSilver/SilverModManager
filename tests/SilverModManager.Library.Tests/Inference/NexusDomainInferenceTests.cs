using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.Inference;

/// <summary>
/// A Nexus Mods item names a Nexus id, and sibling games share one (Skyrim SE and Skyrim VR). With nothing better to go
/// on it is for the games of that id the user manages, marked shared because Nexus cannot say which.
/// </summary>
public class NexusDomainInferenceTests(IConnection connection)
{
    private readonly InferenceFixtures _fixtures = new(connection);

    private async Task<LibraryItem.ReadOnly> AddModFile(NexusModsGameId nexusId)
    {
        return await _fixtures.AddLegacyItem((tx, id) =>
        {
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From((uint)Random.Shared.Next(1, int.MaxValue)), nexusId),
                Name = "A mod",
                GameDomain = GameDomain.From("inference-" + nexusId.Value),
                UpdatedAt = DateTime.UnixEpoch,
                DataUpdatedAt = DateTime.UnixEpoch,
            };
            var metadata = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From((uint)Random.Shared.Next(1, int.MaxValue)), nexusId),
                Name = "A file",
                Version = "1",
                UploadedAt = DateTime.UnixEpoch,
                ModPageId = page,
            };
            tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
            tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        });
    }

    private async Task<LibraryItem.ReadOnly> AddCollectionFile(NexusModsGameId nexusId)
    {
        var slug = CollectionSlug.From("inference-" + Guid.NewGuid().ToString("N"));
        var number = RevisionNumber.From(3);
        using (var tx = connection.BeginTransaction())
        {
            var author = new User.New(tx) { Name = "Author", NexusId = (uint)Random.Shared.Next(1, int.MaxValue) };
            var collection = new CollectionMetadata.New(tx)
            {
                CollectionId = CollectionId.From((uint)Random.Shared.Next(1, int.MaxValue)),
                Slug = slug,
                Name = "A collection",
                GameId = nexusId,
                AuthorId = author,
            };
            _ = new CollectionRevisionMetadata.New(tx)
            {
                RevisionId = RevisionId.From((uint)Random.Shared.Next(1, int.MaxValue)),
                RevisionNumber = number,
                CollectionId = collection,
            };
            await tx.Commit();
        }

        return await _fixtures.AddLegacyItem((tx, id) =>
        {
            tx.Add(id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
            tx.Add(id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, number);
        });
    }

    private async Task Manage(GameId game, NexusModsGameId nexusId) => await _fixtures.CreateLoadout(game, nexusId);

    [Fact]
    public void RunsLast() => new NexusDomainInference([]).Priority.Should().Be(40);

    [Fact]
    public async Task AnItemWithNoOtherEvidence_IsForEveryManagedGameOfTheId_Shared()
    {
        var nexus = InferenceFixtures.NextNexusId();
        var se = InferenceFixtures.UniqueGame("se");
        var vr = InferenceFixtures.UniqueGame("vr");
        await Manage(se, nexus);
        await Manage(vr, nexus);
        var inference = new NexusDomainInference([InferenceFixtures.Game(se, nexus), InferenceFixtures.Game(vr, nexus)]);

        var item = await AddModFile(nexus);

        var destination = inference.Infer(connection.Db, item);

        destination.Games.Should().BeEquivalentTo([se, vr]);
        destination.Shared.Should().BeTrue();
    }

    [Fact]
    public async Task OnlyTheManagedGamesOfTheIdCount()
    {
        var nexus = InferenceFixtures.NextNexusId();
        var managed = InferenceFixtures.UniqueGame("managed");
        var unmanaged = InferenceFixtures.UniqueGame("unmanaged");
        await Manage(managed, nexus);
        var inference = new NexusDomainInference([InferenceFixtures.Game(managed, nexus), InferenceFixtures.Game(unmanaged, nexus)]);

        var item = await AddModFile(nexus);

        var destination = inference.Infer(connection.Db, item);

        destination.Should().Be(Destination.Of(managed), "one game is not a guess between siblings");
    }

    [Fact]
    public async Task WithNothingManaged_TheRegisteredGamesOfTheIdAreUsed_Shared()
    {
        var nexus = InferenceFixtures.NextNexusId();
        var se = InferenceFixtures.UniqueGame("se");
        var vr = InferenceFixtures.UniqueGame("vr");
        var inference = new NexusDomainInference(
            [InferenceFixtures.Game(se, nexus), InferenceFixtures.Game(vr, nexus), InferenceFixtures.Game(InferenceFixtures.UniqueGame("other"), InferenceFixtures.NextNexusId())]);

        var item = await AddModFile(nexus);

        var destination = inference.Infer(connection.Db, item);

        destination.Games.Should().BeEquivalentTo([se, vr]);
        destination.Shared.Should().BeTrue();
    }

    [Fact]
    public async Task AnIdNoGameHas_HasNoAnswer()
    {
        var inference = new NexusDomainInference([InferenceFixtures.Game(InferenceFixtures.UniqueGame("other"), InferenceFixtures.NextNexusId())]);

        var item = await AddModFile(InferenceFixtures.NextNexusId());

        inference.Infer(connection.Db, item).IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task ANexusCollectionFile_IsForTheManagedGamesOfItsCollectionsId()
    {
        var nexus = InferenceFixtures.NextNexusId();
        var managed = InferenceFixtures.UniqueGame("managed");
        await Manage(managed, nexus);
        var inference = new NexusDomainInference([InferenceFixtures.Game(managed, nexus), InferenceFixtures.Game(InferenceFixtures.UniqueGame("other"), InferenceFixtures.NextNexusId())]);

        var item = await AddCollectionFile(nexus);

        var destination = inference.Infer(connection.Db, item);

        destination.Should().Be(Destination.Of(managed));
    }

    [Fact]
    public async Task AnItemThatIsNotFromNexusMods_HasNoAnswer()
    {
        var inference = new NexusDomainInference([InferenceFixtures.Game(InferenceFixtures.UniqueGame("any"), InferenceFixtures.NextNexusId())]);

        var item = await _fixtures.AddLegacyItem();

        inference.Infer(connection.Db, item).IsUnknown.Should().BeTrue();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddGameInstallMetadataModel()
                .AddLoadoutAbstractions()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
