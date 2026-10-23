using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

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
    public async Task Test_AnOnDiskHostRegistersNoStoreLocators()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        TestHostGameStoreGuard.AssertIsolated(provider);
    }
}
