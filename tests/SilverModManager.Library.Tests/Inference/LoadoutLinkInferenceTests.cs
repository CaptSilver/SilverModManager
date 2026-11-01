using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Inference;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Library.Tests.Inference;

/// <summary>A loadout that holds an item says what game the item is for, whatever Nexus Mods calls it.</summary>
public class LoadoutLinkInferenceTests(IConnection connection)
{
    private readonly InferenceFixtures _fixtures = new(connection);
    private readonly LoadoutLinkInference _inference = new();

    [Fact]
    public void RunsFirst() => _inference.Priority.Should().Be(10);

    [Fact]
    public async Task AnItemInstalledInOneLoadout_IsForThatLoadoutsGame()
    {
        var nexus = InferenceFixtures.NextNexusId();
        var vr = InferenceFixtures.UniqueGame("vr");
        var item = await _fixtures.AddLegacyItem();
        await _fixtures.Install(await _fixtures.CreateLoadout(vr, nexus), item);

        var destination = _inference.Infer(connection.Db, item);

        destination.Should().Be(Destination.Of(vr));
    }

    [Fact]
    public async Task AnItemInstalledInLoadoutsOfTwoGames_IsForBothAndNotShared()
    {
        var first = InferenceFixtures.UniqueGame("first");
        var second = InferenceFixtures.UniqueGame("second");
        var item = await _fixtures.AddLegacyItem();
        await _fixtures.Install(await _fixtures.CreateLoadout(first), item);
        await _fixtures.Install(await _fixtures.CreateLoadout(second), item);

        var destination = _inference.Infer(connection.Db, item);

        destination.Games.Should().BeEquivalentTo([first, second]);
        destination.Shared.Should().BeFalse();
    }

    [Fact]
    public async Task AnItemInstalledNowhere_HasNoAnswer()
    {
        var item = await _fixtures.AddLegacyItem();

        _inference.Infer(connection.Db, item).IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task AnItemInstalledOverAnInstallationWithoutAGame_HasNoAnswer()
    {
        var item = await _fixtures.AddLegacyItem();
        await _fixtures.Install(await _fixtures.CreateLoadout(game: null), item);

        _inference.Infer(connection.Db, item).IsUnknown.Should().BeTrue();
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
