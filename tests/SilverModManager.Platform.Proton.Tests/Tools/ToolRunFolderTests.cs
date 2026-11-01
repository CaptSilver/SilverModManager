using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.ToolRunner;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// A tool that writes to a folder it is given gets one inside the prefix's <c>drive_c</c>, which every Wine
/// strategy can see, and Windows programs are handed it as a <c>C:\</c> path.
/// </summary>
public class ToolRunFolderTests : IDisposable
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 1, 9, 5, 7, TimeSpan.Zero);
    private readonly AbsolutePath _prefix = FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine("run-folder-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (_prefix.DirectoryExists()) _prefix.DeleteDirectory(recursive: true);
    }

    [Fact]
    public void TheFolderIsNamedAfterTheToolAndTheStartTimeUnderDriveC()
    {
        var folder = ToolRunFolder.Create(_prefix, new ToolId("dyndolod"), Moment);

        folder.GetFullPath().Should().Be((_prefix / "drive_c/SilverModManager/tool-output/dyndolod/20261101-090507").GetFullPath());
        folder.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void AFolderLeftByAnEarlierRunInTheSameSecondIsNotReused()
    {
        var first = ToolRunFolder.Create(_prefix, new ToolId("dyndolod"), Moment);
        File.WriteAllText((first / "left-over.esp").GetFullPath(), "stale");

        var second = ToolRunFolder.Create(_prefix, new ToolId("dyndolod"), Moment);

        second.Should().NotBe(first);
        second.EnumerateFiles().Should().BeEmpty();
    }

    [Fact]
    public void TheWindowsProgramSeesItAsACDrivePath()
    {
        var folder = ToolRunFolder.Create(_prefix, new ToolId("texgen"), Moment);

        WindowsPathTranslator.Translate("-O:" + folder.GetFullPath(), _prefix.GetFullPath())
            .Should().Be(@"-O:C:\SilverModManager\tool-output\texgen\20261101-090507");
    }
}
