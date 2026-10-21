using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NexusMods.DataModel.SchemaVersions.Tests;

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
}
