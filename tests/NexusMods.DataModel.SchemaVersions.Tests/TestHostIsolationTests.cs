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
}
