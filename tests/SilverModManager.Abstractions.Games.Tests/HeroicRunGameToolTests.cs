using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Tests;

/// <summary>A game Heroic owns goes through the Heroic starter, inside the launch session.</summary>
public class HeroicRunGameToolTests(ITestOutputHelper helper) : AIsolatedGameTest<HeroicRunGameToolTests, StubbedGame>(helper)
{
    private readonly List<string> _commands = [];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.When(r => r.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>()))
            .Do(call => _commands.Add($"{call.Arg<CliWrap.Command>().TargetFilePath} {call.Arg<CliWrap.Command>().Arguments}"));
        runner.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(Task.FromResult(new CliWrap.CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var exes = Substitute.For<IExecutableLocator>();
        exes.Find("heroic").Returns(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/usr/bin/heroic"));
        var versions = Substitute.For<IHeroicVersionProbe>();
        versions.Read(Arg.Any<bool>()).Returns((Version?)null);

        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.EGS])
            {
                StoreClientFor = _ => new StoreClient(StoreClientKind.Heroic, "Sugar", false),
            })
            .AddSingleton(runner)
            .AddSingleton(exes)
            .AddSingleton(versions)
            .ReplaceDesktopNotifier();
    }

    private FakeGameLaunchSession Session => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();
    private RecordingDesktopNotifier Notifier => (RecordingDesktopNotifier)ServiceProvider.GetRequiredService<IDesktopNotifier>();

    private sealed class FastTool(IServiceProvider provider, StubbedGame game) : RunGameTool<StubbedGame>(provider, game)
    {
        protected override TimeSpan StartTimeout => TimeSpan.FromMilliseconds(60);
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(5);
        protected override TimeSpan Settle => TimeSpan.FromMilliseconds(20);
    }

    [Fact]
    public async Task HeroicLaunch_ThatNeverStartsTheGame_TimesOutFinishesAndNotifies()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        _commands.Should().ContainSingle().Which.Should().Contain("--no-gui heroic://launch?appName=Sugar&runner=legendary");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
        Notifier.Notifications.Should().ContainSingle().Which.Body.Should().Contain("Heroic did not start");
    }
}
