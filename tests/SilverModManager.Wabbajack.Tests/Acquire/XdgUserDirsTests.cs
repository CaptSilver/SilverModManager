using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class XdgUserDirsTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared.FromUnsanitizedFullPath(Directory.CreateTempSubdirectory("xdg-dirs-").FullName);
    private AbsolutePath Home => _root.Combine("home");
    private AbsolutePath Config => _root.Combine("config");

    public XdgUserDirsTests()
    {
        Home.CreateDirectory();
        Config.CreateDirectory();
    }

    public void Dispose() => Directory.Delete(_root.ToString(), recursive: true);

    private AbsolutePath Resolve(params string[] lines)
    {
        File.WriteAllText(Config.Combine("user-dirs.dirs").ToString(), string.Join('\n', lines));
        return XdgUserDirs.Download(Home, Config);
    }

    [Fact]
    public void WithoutAFile_ItIsTheDownloadsFolderInHome()
        => XdgUserDirs.Download(Home, Config).Should().Be(Home.Combine("Downloads"));

    [Fact]
    public void AHomeRelativeValueIsResolvedAgainstHome()
        => Resolve("XDG_DOWNLOAD_DIR=\"$HOME/Stuff\"").Should().Be(Home.Combine("Stuff"));

    [Fact]
    public void ANestedHomeRelativeValueKeepsItsSubfolders()
        => Resolve("XDG_DOWNLOAD_DIR=\"$HOME/Stuff/Inbound\"").Should().Be(Home.Combine("Stuff/Inbound"));

    [Fact]
    public void AnAbsoluteValueIsUsedAsWritten()
        => Resolve("XDG_DOWNLOAD_DIR=\"/mnt/dl\"").Should().Be(FileSystem.Shared.FromUnsanitizedFullPath("/mnt/dl"));

    [Theory]
    [InlineData("XDG_DOWNLOAD_DIR=\"Stuff\"")]
    [InlineData("XDG_DOWNLOAD_DIR=\"\"")]
    [InlineData("XDG_DOWNLOAD_DIR=/mnt/dl")]
    [InlineData("XDG_DOWNLOAD_DIR=$HOME/Stuff")]
    [InlineData("# XDG_DOWNLOAD_DIR=\"/mnt/dl\"")]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME\"")]
    [InlineData("XDG_DESKTOP_DIR=\"/mnt/dl\"")]
    public void AValueThatIsNotOneOfTheTwoQuotedForms_FallsBackToDownloads(string line)
        => Resolve(line).Should().Be(Home.Combine("Downloads"));

    [Fact]
    public void OtherKeysCommentsAndBlankLinesAreSkipped()
        => Resolve("# written by xdg-user-dirs-update", "", "XDG_DESKTOP_DIR=\"$HOME/Desk\"", "", "XDG_DOWNLOAD_DIR=\"$HOME/Stuff\"", "XDG_MUSIC_DIR=\"$HOME/Music\"")
            .Should().Be(Home.Combine("Stuff"));
}
