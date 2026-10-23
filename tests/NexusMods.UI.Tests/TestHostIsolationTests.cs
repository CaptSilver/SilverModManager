using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.App;
using NexusMods.Backend;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.UI.Tests;

public class TestHostIsolationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void Test_TheTestHostDoesNotRefreshTheLauncherShim()
    {
        // The test host builds the GUI graph, whose shim refresher would point the user's Steam launcher shim
        // at the test runner.
        serviceProvider.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .Should().NotContain("LauncherShimRefresher");
    }

    [Fact]
    public void Test_TheTestHostDoesNotProbeRuntimeDependencies()
    {
        // The checker runs `flatpak run ... --version` and similar at host start. A test host per run would
        // start real tools on the developer's machine, and a stalled one leaves a process behind.
        serviceProvider.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .Should().NotContain("RuntimeDependencyChecker");
    }

    [Fact]
    public void Test_TheTestHostRegistersNoStoreLocators()
        => TestHostGameStoreGuard.AssertOnlyStubbedLocators(serviceProvider);

    [Fact]
    public void Test_TheTestHostLocatesNothingOutsideTheTestFolders()
        => TestHostGameStoreGuard.AssertInstallationsAreInTestFolders(serviceProvider);

    [Fact]
    public void Test_TheAppsOwnGraphKeepsTheStoreLocatorsWithoutDuplicates()
    {
        // The test host leaves the store locators out, so this is what still checks the graph the app builds.
        // Descriptors only: resolving the locators would read the machine's real Steam and Heroic folders.
        static IServiceCollection Graph(bool addStandardGameLocators) => new ServiceCollection().AddLogging().AddApp(
            addStandardGameLocators,
            new StartupMode { RunAsMain = true, ShowUI = true, ExecuteCli = false });

        static int Locators(IServiceCollection services) => services.Count(d => d.ServiceType == typeof(IGameLocator));

        var app = Graph(addStandardGameLocators: true);
        app.Invoking(services => services.Validate()).Should().NotThrow();
        (Locators(app) - Locators(Graph(addStandardGameLocators: false)))
            .Should().Be(Locators(new ServiceCollection().AddGameLocators()));
    }
}
