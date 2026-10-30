using FluentAssertions;
using SilverModManager.Games.CreationEngine.Installers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

public class StopPatternInstallerBuildTests(IServiceProvider serviceProvider)
{
    /// <summary>
    /// An installer that was configured but never built has no compiled patterns to install with.
    /// </summary>
    [Fact]
    public void ExecuteBeforeBuild_ThrowsNamingTheMissingStep()
    {
        var installer = new StopPatternInstaller(serviceProvider)
        {
            GameId = default,
            GameAliases = [],
            TopLevelDirs = [],
            StopPatterns = [],
            EngineFiles = [],
        };

        var act = () => installer.ExecuteAsync(default, null!, null!, default, CancellationToken.None);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not been built*");
    }
}
