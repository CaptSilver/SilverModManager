using FluentAssertions;
using SilverModManager.Abstractions.Games.Tools;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests.Tools;

public class ToolRunMessageTests
{
    [Fact]
    public void AFinishedRunStatesTheExitCodeAndWhatWasCaptured()
    {
        ToolRunMessage.Finished("DynDOLOD", new ToolRunSummary(0, 1204), "DynDOLOD Output")
            .Should().Be("DynDOLOD finished (exit code 0). Captured 1,204 files into 'DynDOLOD Output'.");
    }

    [Fact]
    public void OneFileIsNotPlural()
    {
        ToolRunMessage.Finished("TexGen", new ToolRunSummary(0, 1), "TexGen Output")
            .Should().Be("TexGen finished (exit code 0). Captured 1 file into 'TexGen Output'.");
    }

    [Fact]
    public void ARunWithAnOutputGroupThatCapturedNothingSaysSo()
    {
        ToolRunMessage.Finished("xEdit", new ToolRunSummary(0, 0), "xEdit Output")
            .Should().Be("xEdit finished (exit code 0). Captured no files into 'xEdit Output'.");
    }

    [Fact]
    public void AToolWithoutAnOutputGroupOnlyStatesTheExitCode()
    {
        ToolRunMessage.Finished("My tool", new ToolRunSummary(3, 0), outputGroup: null)
            .Should().Be("My tool finished (exit code 3).");
    }

    [Fact]
    public void AToolThatReportsNoSummaryJustFinished()
    {
        ToolRunMessage.Finished("LOOT", summary: null, outputGroup: null).Should().Be("LOOT finished.");
    }

    [Fact]
    public void AGameFolderToolsMovedFilesAreAddedToTheCount()
    {
        ToolRunMessage.Finished("BodySlide", new ToolRunSummary(0, 0), "BodySlide Output", movedFromGameFolder: 12)
            .Should().Be("BodySlide finished (exit code 0). Captured 12 files into 'BodySlide Output'.");
    }
}
