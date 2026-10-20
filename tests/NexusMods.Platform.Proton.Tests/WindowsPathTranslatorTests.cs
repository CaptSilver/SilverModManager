using FluentAssertions;
using NexusMods.Platform.Proton.ToolRunner;

namespace NexusMods.Platform.Proton.Tests;

public class WindowsPathTranslatorTests
{
    private const string Prefix = "/home/user/.steam/steam/steamapps/compatdata/1091500/pfx";

    [Fact]
    public void Test_FileUnderDriveCMapsToC()
    {
        WindowsPathTranslator.Translate($"{Prefix}/drive_c/Games/Tool/tool.exe", Prefix)
            .Should().Be(@"C:\Games\Tool\tool.exe");
    }

    [Fact]
    public void Test_DirectoryWithTrailingSlashKeepsSeparator()
    {
        WindowsPathTranslator.Translate($"{Prefix}/drive_c/Games/", Prefix)
            .Should().Be(@"C:\Games\");
    }

    [Fact]
    public void Test_DriveCRootMapsToCRoot()
    {
        WindowsPathTranslator.Translate($"{Prefix}/drive_c", Prefix).Should().Be(@"C:\");
        WindowsPathTranslator.Translate($"{Prefix}/drive_c/", Prefix).Should().Be(@"C:\");
    }

    [Fact]
    public void Test_PrefixRootWithTrailingSlashStillMatches()
    {
        WindowsPathTranslator.Translate($"{Prefix}/drive_c/a.txt", Prefix + "/").Should().Be(@"C:\a.txt");
    }

    [Fact]
    public void Test_SiblingOfDriveCIsNotTreatedAsDriveC()
    {
        WindowsPathTranslator.Translate($"{Prefix}/drive_c2/a.txt", Prefix)
            .Should().Be(@"Z:\home\user\.steam\steam\steamapps\compatdata\1091500\pfx\drive_c2\a.txt");
    }

    [Fact]
    public void Test_HostPathOutsidePrefixMapsToZ()
    {
        WindowsPathTranslator.Translate("/tmp/output.txt", Prefix).Should().Be(@"Z:\tmp\output.txt");
    }

    [Fact]
    public void Test_PathWithSpacesIsKeptVerbatim()
    {
        WindowsPathTranslator.Translate("/home/user/My Mods/a b.esp", Prefix)
            .Should().Be(@"Z:\home\user\My Mods\a b.esp");
        WindowsPathTranslator.Translate($"{Prefix}/drive_c/Program Files/x y.exe", Prefix)
            .Should().Be(@"C:\Program Files\x y.exe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-quickAutoClean")]
    [InlineData("--output=/tmp/x")]
    [InlineData("Data/plugin.esp")]
    [InlineData("plain")]
    public void Test_NonAbsolutePathArgumentsAreLeftAlone(string argument)
    {
        WindowsPathTranslator.Translate(argument, Prefix).Should().Be(argument);
    }
}
