using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam.Shortcuts;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamGridArtTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-grid-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Dir(string name) => FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(_root, name));

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void GridFilesLandUnderTheSixNames()
    {
        const uint appId = 0x8123_4567;
        var launchId = SteamShortcutId.LaunchId(appId);

        var written = SteamGridArt.Write(Dir("grid"), appId);

        written.Select(p => p.FileName).Should().BeEquivalentTo(
            $"{appId}p.png", $"{appId}.png", $"{appId}_hero.png", $"{appId}_logo.png", $"{appId}_icon.png", $"{launchId}.png");
        foreach (var path in written)
        {
            File.ReadAllBytes(path.ToString()).Take(8).Should().Equal(PngMagic, path.ToString());
        }
    }

    [Fact]
    public void NoTempFilesAreLeftBehind()
    {
        SteamGridArt.Write(Dir("grid"), 0x8123_4567);

        Directory.GetFiles(Dir("grid").ToString()).Should().NotContain(f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void TheIconIsExtractedToTheIconsFolder()
    {
        var icon = SteamGridArt.WriteIcon(Dir("icons"));

        icon.FileName.Should().Be("silvermodmanager.png");
        File.ReadAllBytes(icon.ToString()).Take(8).Should().Equal(PngMagic);
    }

    [Fact]
    public void AGridFolderThatCannotBeCreatedIsAnIoErrorNotASilentSkip()
    {
        File.WriteAllText(System.IO.Path.Combine(_root, "blocker"), "x");

        var act = () => SteamGridArt.Write(Dir("blocker/grid"), 1);

        act.Should().Throw<IOException>();
    }
}
