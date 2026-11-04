using FluentAssertions;
using SilverModManager.Platform.Proton.ToolRunner;

namespace SilverModManager.Platform.Proton.Tests;

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

    [Fact]
    public void Test_PathAfterAFlagPrefixIsTranslated()
    {
        WindowsPathTranslator.Translate("-D:/game/data", Prefix).Should().Be(@"-D:Z:\game\data");
    }

    [Fact]
    public void Test_QuotedPathAfterAFlagPrefixIsTranslated()
    {
        WindowsPathTranslator.Translate("-D:\"/game/my data\"", Prefix).Should().Be("-D:\"Z:\\game\\my data\"");
    }

    [Fact]
    public void Test_QuotedPathInsideALongerArgumentIsTranslated()
    {
        WindowsPathTranslator.Translate($"-D:\"/game/data\" -O:\"{Prefix}/drive_c/out\" -q", Prefix)
            .Should().Be("-D:\"Z:\\game\\data\" -O:\"C:\\out\" -q");
    }

    [Fact]
    public void Test_FlagsAndQuotedWordsWithoutPathsAreUntouched()
    {
        WindowsPathTranslator.Translate("-quickautoclean", Prefix).Should().Be("-quickautoclean");
        WindowsPathTranslator.Translate("-D:data", Prefix).Should().Be("-D:data");
        WindowsPathTranslator.Translate("\"two words\"", Prefix).Should().Be("\"two words\"");
    }

    [Theory]
    [InlineData("{P}/drive_c/Games/Old/../Tool/tool.exe", @"C:\Games\Tool\tool.exe")]
    [InlineData("{P}/drive_c/Games/./Tool/", @"C:\Games\Tool\")]
    [InlineData("{P}/drive_c/Games/..", @"C:\")]
    [InlineData("{P}/x/../drive_c/f", @"C:\f")]
    public void Test_DotSegmentsUnderDriveCResolveOnTheHostPath(string argument, string expected)
    {
        WindowsPathTranslator.Translate(argument.Replace("{P}", Prefix), Prefix).Should().Be(expected);
    }

    [Theory]
    [InlineData("{P}/drive_c/..", @"Z:\home\user\.steam\steam\steamapps\compatdata\1091500\pfx")]
    [InlineData("{P}/drive_c/../user.reg", @"Z:\home\user\.steam\steam\steamapps\compatdata\1091500\pfx\user.reg")]
    [InlineData("{P}/drive_c/a/../../../b", @"Z:\home\user\.steam\steam\steamapps\compatdata\1091500\b")]
    public void Test_DotSegmentsClimbingOutOfDriveCLandOnTheHostFile(string argument, string expected)
    {
        WindowsPathTranslator.Translate(argument.Replace("{P}", Prefix), Prefix).Should().Be(expected);
    }

    [Theory]
    [InlineData("/tmp/a/../b", @"Z:\tmp\b")]
    [InlineData("/tmp/./a", @"Z:\tmp\a")]
    [InlineData("/tmp/../../../x", @"Z:\x")]
    [InlineData("/tmp/a/..", @"Z:\tmp")]
    [InlineData("/..", @"Z:\")]
    public void Test_DotSegmentsInAHostPathResolveAndStopAtTheHostRoot(string argument, string expected)
    {
        WindowsPathTranslator.Translate(argument, Prefix).Should().Be(expected);
    }

    [Fact]
    public void Test_DotSegmentsAreResolvedAfterAFlagPrefixAndInsideQuotes()
    {
        WindowsPathTranslator.Translate("-D:/game/data/../mods", Prefix).Should().Be(@"-D:Z:\game\mods");
        WindowsPathTranslator.Translate($"-O:\"{Prefix}/drive_c/out/../../..\" -q", Prefix).Should().Be("-O:\"Z:\\home\\user\\.steam\\steam\\steamapps\\compatdata\\1091500\" -q");
    }

    [Fact]
    public void Test_SlashCDirectoryIsAnOrdinaryHostPath()
    {
        // Only a path under the prefix's drive_c is a C: path; /c/... is a folder named c on the host.
        WindowsPathTranslator.Translate("/c/Users/me", Prefix).Should().Be(@"Z:\c\Users\me");
    }

    [Theory]
    [InlineData("C:/Games/tool.exe")]
    [InlineData(@"c:\Games\tool.exe")]
    [InlineData(@"C:\Games\..\tool.exe")]
    public void Test_ArgumentAlreadyInWindowsFormIsLeftAlone(string argument)
    {
        WindowsPathTranslator.Translate(argument, Prefix).Should().Be(argument);
    }
}
