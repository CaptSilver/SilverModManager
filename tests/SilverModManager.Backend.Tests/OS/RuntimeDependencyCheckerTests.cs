using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Sdk;
using NSubstitute;

namespace NexusMods.Backend.Tests.OS;

public class RuntimeDependencyCheckerTests
{
    private static IHost BuildHost(IRuntimeDependency spy, bool removeChecker)
        => new HostBuilder().ConfigureServices(services =>
        {
            services
                .AddLogging()
                // The real dependencies are constructed with the graph but never queried, so a fake runner is enough.
                .AddSingleton(Substitute.For<IProcessRunner>())
                .AddRuntimeDependencies()
                .AddSingleton(spy);

            if (removeChecker) services.RemoveRuntimeDependencyChecker();
        }).Build();

    private static IRuntimeDependency Spy()
    {
        var spy = Substitute.For<IRuntimeDependency>();
        spy.DisplayName.Returns("spy");
        spy.QueryInstallationInformation(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Optional<RuntimeDependencyInformation>.None));
        return spy;
    }

    [Fact]
    public async Task StartAsync_WithTheCheckerRegistered_QueriesEveryDependency()
    {
        // Keeps the next test honest: the spy does see a probe when nothing removes the checker.
        var spy = Spy();
        using var host = BuildHost(spy, removeChecker: false);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await spy.Received().QueryInstallationInformation(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WithTheCheckerRemoved_QueriesNothing()
    {
        var spy = Spy();
        using var host = BuildHost(spy, removeChecker: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await spy.DidNotReceive().QueryInstallationInformation(Arg.Any<CancellationToken>());
    }
}
