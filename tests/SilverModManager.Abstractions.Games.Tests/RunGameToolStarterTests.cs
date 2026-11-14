using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Tests;

/// <summary>
/// A game whose store client cannot carry the launch's arguments, or that has no client at all (the locator
/// reports it as a Steam install unless a test says otherwise, since the stubbed store backend only knows one):
/// the launch still goes through a session and starts the program itself.
/// </summary>
public abstract class RunGameToolStarterTests(ITestOutputHelper helper) : AIsolatedGameTest<RunGameToolStarterTests, StubbedGame>(helper)
{
    protected abstract StoreClient? Client { get; }

    protected virtual GameStore Store => GameStore.Steam;

    /// <summary>The arguments the launch is given.</summary>
    protected virtual string[] Arguments => [];

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [Store])
            {
                LocatorIds = [LocatorId.From("3976631895")],
                StoreClientFor = _ => Client,
            })
            .ReplaceDesktopNotifier();

    protected FakeGameLaunchSession Session => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();
    protected RecordingDesktopNotifier Notifier => (RecordingDesktopNotifier)ServiceProvider.GetRequiredService<IDesktopNotifier>();
    protected FakeToolRunner ToolRunner => ServiceProvider.GetRequiredService<FakeToolRunner>();

    protected sealed class FastTool(IServiceProvider provider, StubbedGame game) : RunGameTool<StubbedGame>(provider, game)
    {
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(5);
        protected override TimeSpan Settle => TimeSpan.FromMilliseconds(20);
    }

    [Fact]
    public async Task EveryStore_GoesThroughBeginSession_WithTheSpecOfThePrimaryExe()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, Arguments);

        Session.BegunWith.Should().ContainSingle().Which.Should().NotBeNull();
        Session.Events.Should().Equal("lock", "running", "finish", "release");
        ToolRunner.Calls.Should().ContainSingle("the program is started directly");
    }
}

public sealed class RunGameToolWithoutStoreClientTests(ITestOutputHelper helper) : RunGameToolStarterTests(helper)
{
    protected override StoreClient? Client => null;

    [Fact]
    public async Task AStoreWithoutAClient_StartsDirectlyWithNoNotice()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        Notifier.Notifications.Should().BeEmpty();
    }
}

public sealed class RunGameToolWithAClientThatCannotCarryTheArgumentsTests(ITestOutputHelper helper) : RunGameToolStarterTests(helper)
{
    protected override StoreClient? Client => new(StoreClientKind.Heroic, "some-game", false);

    protected override GameStore Store => GameStore.EGS;

    // The query form of Heroic's launch URL, which carries arguments, starts with 2.16.
    protected override string[] Arguments => ["-modded"];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var versions = Substitute.For<IHeroicVersionProbe>();
        versions.Read(Arg.Any<bool>()).Returns(new Version(2, 15, 2));
        return base.AddServices(services).AddSingleton(versions);
    }

    [Fact]
    public async Task ADirectFallbackForAClientInstall_NotifiesInsideTheSession()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        string[]? eventsAtNotice = null;
        int notificationsAtStart = -1;
        Notifier.OnNotify = (_, _) =>
        {
            lock (Session.Events) eventsAtNotice = Session.Events.ToArray();
        };
        ToolRunner.OnRun = () => notificationsAtStart = Notifier.Notifications.Count;

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, Arguments);

        eventsAtNotice.Should().Equal(["lock", "running"], "the notice is shown once the session holds the lock and has applied the loadout");
        notificationsAtStart.Should().Be(1, "the notice comes before the program runs");
        Notifier.Notifications.Should().ContainSingle().Which.Body.Should().Contain("Heroic").And.Contain("-modded").And.Contain("cloud saves");
    }
}
