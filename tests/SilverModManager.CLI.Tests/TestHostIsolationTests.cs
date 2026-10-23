using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

public class TestHostIsolationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void Test_TheTestHostIsIsolatedFromTheMachinesGameStores()
        => TestHostGameStoreGuard.AssertIsolated(serviceProvider);
}
