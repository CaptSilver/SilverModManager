using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

public class TestHostIsolationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void Test_TheTestHostIsIsolatedFromTheMachinesGameStores()
        => TestHostGameStoreGuard.AssertIsolated(serviceProvider);
}
