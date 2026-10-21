using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// Pins the set of game locators the app ships, so a store that was removed can't be
/// registered again by accident.
/// </summary>
public class GameLocatorRegistrationTests
{
    [Fact]
    public void AddGameLocators_RegistersExactlyTheLinuxLocatorSet()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix))
            .AddSingleton<IEnumerable<IGameData>>([])
            .AddGameLocators();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IGameLocator>().Select(locator => locator.GetType()).Should().Equal(
            typeof(SteamLocator),
            typeof(HeroicGOGLocator),
            typeof(HeroicEpicLocator),
            typeof(WinePrefixWrappingLocator)
        );
    }

    [Fact]
    public void AddGameServices_RegistersTheUserInstallLocatorAndNoManuallyAddedLocator()
    {
        var services = new ServiceCollection().AddGameServices();

        services
            .Where(descriptor => descriptor.ServiceType == typeof(IGameLocator))
            .Select(descriptor => descriptor.ImplementationType)
            .Should().Equal(typeof(UserInstallLocator));
    }
}
