using FluentAssertions;
using SilverModManager.App;
using AppStartup = SilverModManager.App.Startup;

namespace SilverModManager.CLI.Tests;

public class StartupLifecycleTests
{
    [Fact]
    public void Provider_BeforeMainNamesTheStartupClass()
    {
        var read = () => AppStartup.Provider;

        read.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Startup");
    }

    [Fact]
    public void Logger_BeforeMainNamesTheStartupClass()
    {
        var read = () => AppStartup.Logger;

        read.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Startup");
    }

    [Fact]
    public void ProgramLogger_BeforeTheHostIsBuiltNamesTheProgramClass()
    {
        var read = () => Program.Logger;

        read.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Program");
    }
}
