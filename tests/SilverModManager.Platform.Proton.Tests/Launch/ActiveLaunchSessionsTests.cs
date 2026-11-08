using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Abstractions.Loadouts;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class ActiveLaunchSessionsTests
{
    private static GameInstallation Installation(string storeId)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-active-sessions-" + storeId);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = storeId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    [Fact]
    public void IsRunning_IsFalse_ForAnInstallationNobodyRegistered()
    {
        new ActiveLaunchSessions().IsRunning(Installation("1")).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_IsTrue_FromRegisterUntilTheRegistrationIsDisposed()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");

        var registration = sessions.Register(installation);
        sessions.IsRunning(installation).Should().BeTrue();

        registration.Dispose();
        sessions.IsRunning(installation).Should().BeFalse();
    }

    [Fact]
    public void IsFinishing_FollowsTheReportedPhase()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        var registration = sessions.Register(installation);

        sessions.IsFinishing(installation).Should().BeFalse();
        sessions.ReportPhase(installation, LaunchPhase.Finishing);
        sessions.IsFinishing(installation).Should().BeTrue();
        sessions.ReportPhase(installation, LaunchPhase.Preparing);
        sessions.IsFinishing(installation).Should().BeFalse();

        sessions.ReportPhase(installation, LaunchPhase.Finishing);
        registration.Dispose();
        sessions.IsFinishing(installation).Should().BeFalse();
    }

    [Fact]
    public void WhenRunning_EmitsTheCurrentValueThenTrueOnRegisterAndFalseOnceTheLastRegistrationIsDisposed()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        var seen = new List<bool>();
        using var subscription = sessions.WhenRunning(installation).Subscribe(seen.Add);
        seen.Should().Equal(false);

        var first = sessions.Register(installation);
        var second = sessions.Register(installation);
        first.Dispose();
        seen.Should().Equal(false, true);

        second.Dispose();
        seen.Should().Equal(false, true, false);
    }

    [Fact]
    public void WhenRunning_StartsTrueForAnInstallationThatIsAlreadyRunning()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        using var registration = sessions.Register(installation);

        sessions.WhenRunning(installation).FirstAsync().Wait().Should().BeTrue();
    }

    [Fact]
    public void WhenRunning_IgnoresAnotherInstallation()
    {
        var sessions = new ActiveLaunchSessions();
        var seen = new List<bool>();
        using var subscription = sessions.WhenRunning(Installation("1")).Subscribe(seen.Add);

        using var other = sessions.Register(Installation("2"));

        seen.Should().Equal(false);
    }

    [Fact]
    public void IsRunning_IsPerInstallation()
    {
        var sessions = new ActiveLaunchSessions();

        using var _ = sessions.Register(Installation("1"));

        sessions.IsRunning(Installation("2")).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_MatchesAnotherInstanceOfTheSameInstallation()
    {
        // A rescan builds new GameInstallation objects, and the writers look the installation up through the
        // registry rather than holding the one the launch registered.
        var sessions = new ActiveLaunchSessions();

        using var _ = sessions.Register(Installation("1"));

        sessions.IsRunning(Installation("1")).Should().BeTrue();
    }

    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotEndAnotherRegistration()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        var first = sessions.Register(installation);
        using var second = sessions.Register(installation);

        first.Dispose();
        first.Dispose();

        sessions.IsRunning(installation).Should().BeTrue();
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheRegistryUnderBothTypes()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        services.AddLogging();
        services.AddSingleton(Substitute.For<IProcessRunner>());
        services.AddSingleton(Substitute.For<IConnection>());
        services.AddSingleton(Substitute.For<IGameRegistry>());
        services.AddSingleton(Substitute.For<IFileSystem>());
        services.AddSingleton(Substitute.For<IToolManager>());
        services.AddSingleton(Substitute.For<IJobMonitor>());
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IGameLaunchSession>().Should().BeSameAs(provider.GetRequiredService<ActiveLaunchSessions>());
    }

    [Fact]
    public void RegisterFinish_CountsAsRunning_ButItsEndIsNotALaunchEnding()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        var ended = 0;
        using var subscription = sessions.WhenLaunchEnded(installation).Subscribe(_ => ended++);

        var finish = sessions.RegisterFinish(installation);
        sessions.IsRunning(installation).Should().BeTrue();
        finish.Dispose();

        sessions.IsRunning(installation).Should().BeFalse();
        ended.Should().Be(0);
    }

    [Fact]
    public void WhenLaunchEnded_EmitsWhenTheLastLaunchRegistrationIsDisposed_OnlyForThatInstallation()
    {
        var sessions = new ActiveLaunchSessions();
        var installation = Installation("1");
        var ended = 0;
        using var subscription = sessions.WhenLaunchEnded(installation).Subscribe(_ => ended++);

        var first = sessions.Register(installation);
        var second = sessions.Register(installation);
        sessions.Register(Installation("2")).Dispose();
        first.Dispose();
        ended.Should().Be(0, "a launch is still registered");
        second.Dispose();

        ended.Should().Be(1);
    }
}
