using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Install;
using Xunit;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>Whether a tool counts as inside the tools folder when the two are spelled through different symlinks.</summary>
public class ModlistToolsContainmentTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "tools-containment-" + Guid.NewGuid().ToString("N"));
    private readonly string _real;
    private readonly string _link;

    public ModlistToolsContainmentTests()
    {
        _real = Path.Combine(_scratch, "var", "extras");
        _link = Path.Combine(_scratch, "extras-link");
        Directory.CreateDirectory(Path.Combine(_real, "xEdit"));
        File.WriteAllText(Path.Combine(_real, "xEdit", "SSEEdit.exe"), "tool");
        Directory.CreateDirectory(Path.Combine(_scratch, "game"));
        Directory.CreateSymbolicLink(_link, _real);
    }

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private static AbsolutePath Absolute(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private ModlistTools.Result Find(string toolsFolder, string binary)
        => ModlistTools.Find(
            $"[customExecutables]\nsize=1\n1\\arguments=\n1\\binary={binary}\n1\\title=SSEEdit\n",
            Absolute(toolsFolder), Absolute(Path.Combine(_scratch, "game")), "Game.exe", _ => null, _ => null);

    [Fact]
    public void AToolSpelledThroughTheRealFolderIsInsideAToolsFolderGivenAsALink()
    {
        var result = Find(_link, Path.Combine(_real, "xEdit", "SSEEdit.exe"));

        result.Unregistered.Should().BeEmpty();
        result.Tools.Should().ContainSingle().Which.Exe.Should().Be(Path.Combine(_real, "xEdit", "SSEEdit.exe"));
    }

    [Fact]
    public void AToolSpelledThroughTheLinkIsInsideAToolsFolderGivenAsTheRealFolder()
    {
        var result = Find(_real, Path.Combine(_link, "xEdit", "SSEEdit.exe"));

        result.Unregistered.Should().BeEmpty();
        result.Tools.Should().ContainSingle();
    }

    [Fact]
    public void ADifferentFolderBesideTheToolsFolderIsStillOutside()
    {
        var other = Path.Combine(_scratch, "var", "extras-other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Tool.exe"), "tool");

        var result = Find(_link, Path.Combine(other, "Tool.exe"));

        result.Tools.Should().BeEmpty();
        result.Unregistered.Should().ContainSingle();
    }

    [Fact]
    public void AToolThatIsNotOnDiskIsReportedAsMissing()
    {
        var result = Find(_link, Path.Combine(_link, "Missing", "Tool.exe"));

        var message = result.Unregistered.Should().ContainSingle().Subject;
        message.Should().Contain("does not exist");
        message.Should().NotContain("outside");
    }

    [Fact]
    public void AToolThatIsOnDiskButOutsideBothFoldersIsReportedAsOutside()
    {
        var other = Path.Combine(_scratch, "elsewhere");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Tool.exe"), "tool");

        var result = Find(_link, Path.Combine(other, "Tool.exe"));

        var message = result.Unregistered.Should().ContainSingle().Subject;
        message.Should().Contain("outside the list's tools folder and the game folder");
        message.Should().NotContain("does not exist");
    }
}
