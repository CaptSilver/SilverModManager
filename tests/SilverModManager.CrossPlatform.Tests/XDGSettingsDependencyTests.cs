using CliWrap;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Sdk;
using SilverModManager.Backend.RuntimeDependency;

namespace SilverModManager.CrossPlatform.Tests;

public class XDGSettingsDependencyTests
{
    [Theory]
    [InlineData("xdg-settings 1.2.1\n", "1.2.1")]
    [InlineData("xdg-settings 1.2.1", "1.2.1")]
    [InlineData("xdg-settings", null)]
    public void TestTryParseVersion(string input, string? expectedRawVersion)
    {
        _ = XdgSettingsDependency.TryParseVersion(input, out var rawVersion, out _);
        rawVersion.Should().Be(expectedRawVersion);
    }

    [Fact]
    public void TheGetCommandAsksForTheDefaultHandlerOfTheScheme()
    {
        var command = new XdgSettingsDependency(Substitute.For<IProcessRunner>()).CreateGetDefaultUrlSchemeHandlerCommand("ror2mm");

        command.TargetFilePath.Should().Be("xdg-settings");
        command.Arguments.Should().Be("get default-url-scheme-handler ror2mm");
    }
}
