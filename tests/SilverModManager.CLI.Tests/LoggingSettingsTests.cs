using FluentAssertions;
using SilverModManager.CrossPlatform;
using SilverModManager.Sdk;

namespace SilverModManager.CLI.Tests;

public class LoggingSettingsTests
{
    [Fact]
    public void EveryProcessLogFilePathIsListed()
    {
        var settings = LoggingSettings.CreateDefault();

        var declared = typeof(LoggingSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(ConfigurablePath) && p.Name.EndsWith("ProcessLogFilePath", StringComparison.Ordinal))
            .Select(p => (ConfigurablePath)p.GetValue(settings)!)
            .ToArray();

        declared.Should().NotBeEmpty();
        settings.GetProcessLogFilePaths().Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void TheMcpLogIsListed()
    {
        var settings = LoggingSettings.CreateDefault();

        settings.GetProcessLogFilePaths().Should().Contain(settings.McpProcessLogFilePath);
    }
}
