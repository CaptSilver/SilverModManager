using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Networking.Steam.Workshop.DTOs;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The <c>workshop</c> verbs over a stubbed game kept in a Steam library layout, with Steam answered by a substitute and
/// the operating system's link opener replaced so no browser or Steam client starts.
/// </summary>
[Collection("ConsoleOutput")]
public class WorkshopVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<WorkshopVerbsTests, WorkshopStubbedGame>(helper)
{
    private readonly ISteamWorkshopApi _api = Substitute.For<ISteamWorkshopApi>();
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _api.Details(Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<PublishedFileDetails>>(
            [.. call.Arg<IReadOnlyList<ulong>>().Select(id => new PublishedFileDetails(id, 1, $"Item {id}", DateTimeOffset.FromUnixTimeSeconds(4_000_000_000), 1, null, 268500, 0, 0))]));

        base.AddServices(services)
            .AddGame<WorkshopStubbedGame>()
            .AddUniversalGameLocator(WorkshopStubbedGame.GameId, folderName: "steamapps/common/WorkshopStubbedGame")
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton(_api));
        services.Replace(ServiceDescriptor.Singleton(_os));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    private void Subscribe(ulong id)
    {
        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Parent.Parent.Combine($"workshop/content/268500/{id}").CreateDirectory();
    }

    [Fact]
    public async Task ListShowsEachSubscribedItemWithItsTitleAndWhetherSteamUpdatedIt()
    {
        var loadout = await CreateLoadout();
        Subscribe(100);
        Subscribe(200);

        var (code, output) = await Run("workshop", "list", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("Item 100").And.Contain("Item 200").And.Contain("yes");
    }

    [Fact]
    public async Task ListWithNoSubscribedItemsSaysSo()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("workshop", "list", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("holds no items");
        await _api.DidNotReceiveWithAnyArgs().Details(default!, default);
    }

    [Fact]
    public async Task OpenSendsTheCommunityPageToTheBrowserAndTheSteamLinkOnRequest()
    {
        var (code, output) = await Run("workshop", "open", "-i", "555");
        var (steamCode, _) = await Run("workshop", "open", "-i", "555", "-s");

        code.Should().Be(0, output);
        steamCode.Should().Be(0);
        _os.Received(1).OpenUri(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=555"));
        _os.Received(1).OpenUri(new Uri("steam://url/CommunityFilePage/555"));
    }

    [Fact]
    public async Task OpenRefusesAnIdThatIsNotANumber()
    {
        var (code, output) = await Run("workshop", "open", "-i", "harmony");

        code.Should().Be(1);
        output.Should().Contain("not a Workshop item id");
        _os.DidNotReceiveWithAnyArgs().OpenUri(default!);
    }
}
