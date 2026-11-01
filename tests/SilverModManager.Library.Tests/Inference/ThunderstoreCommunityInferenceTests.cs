using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Inference;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.Inference;

/// <summary>A Thunderstore package is for the game whose community it was listed under.</summary>
public class ThunderstoreCommunityInferenceTests(IConnection connection)
{
    private readonly InferenceFixtures _fixtures = new(connection);

    private async Task<LibraryItem.ReadOnly> AddPackage(string community)
    {
        var name = Guid.NewGuid().ToString("N");
        var file = await new LockFixtures(connection).AddThunderstoreFile("team", name, "1.0.0", name, pinned: null, community);
        return LibraryItem.Load(connection.Db, file.Id);
    }

    [Fact]
    public void RunsAfterTheModlistEvidence() => new ThunderstoreCommunityInference([]).Priority.Should().Be(30);

    [Fact]
    public async Task APackage_IsForTheGameOfItsCommunity()
    {
        var community = "community-" + Guid.NewGuid().ToString("N");
        var game = InferenceFixtures.UniqueGame("thunderstore");
        IGameData[] registry =
        [
            InferenceFixtures.Game(InferenceFixtures.UniqueGame("elsewhere"), thunderstoreCommunity: "another-" + community),
            InferenceFixtures.Game(InferenceFixtures.UniqueGame("no-thunderstore")),
            InferenceFixtures.Game(game, thunderstoreCommunity: community),
        ];
        var item = await AddPackage(community);

        new ThunderstoreCommunityInference(registry).Infer(connection.Db, item).Should().Be(Destination.Of(game));
    }

    [Fact]
    public async Task APackageOfACommunityNoGameHas_HasNoAnswer()
    {
        var item = await AddPackage("community-" + Guid.NewGuid().ToString("N"));
        IGameData[] registry = [InferenceFixtures.Game(InferenceFixtures.UniqueGame("thunderstore"), thunderstoreCommunity: "other")];

        new ThunderstoreCommunityInference(registry).Infer(connection.Db, item).IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task AnItemThatIsNotAPackage_HasNoAnswer()
    {
        var item = await _fixtures.AddLegacyItem();
        IGameData[] registry = [InferenceFixtures.Game(InferenceFixtures.UniqueGame("thunderstore"), thunderstoreCommunity: "repo")];

        new ThunderstoreCommunityInference(registry).Infer(connection.Db, item).IsUnknown.Should().BeTrue();
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
