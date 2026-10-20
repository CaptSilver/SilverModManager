using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Launch;

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
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IGameLaunchSession>().Should().BeSameAs(provider.GetRequiredService<ActiveLaunchSessions>());
    }
}
