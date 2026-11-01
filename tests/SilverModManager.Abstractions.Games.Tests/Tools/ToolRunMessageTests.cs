using FluentAssertions;
using SilverModManager.Abstractions.Games.Tools;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests.Tools;

public class ToolRunMessageTests
{
    [Fact]
    public void AFinishedRunStatesTheExitCodeAndWhatWasCaptured()
    {
        ToolRunMessage.Finished("DynDOLOD", 0, "DynDOLOD Output", 1204)
            .Should().Be("DynDOLOD finished (exit code 0). Captured 1,204 files into 'DynDOLOD Output'.");
    }

    [Fact]
    public void OneFileIsNotPlural()
    {
        ToolRunMessage.Finished("TexGen", 0, "TexGen Output", 1)
            .Should().Be("TexGen finished (exit code 0). Captured 1 file into 'TexGen Output'.");
    }

    [Fact]
    public void ARunWithAnOutputGroupThatCapturedNothingSaysSo()
    {
        ToolRunMessage.Finished("xEdit", 0, "xEdit Output", 0)
            .Should().Be("xEdit finished (exit code 0). Captured no files into 'xEdit Output'.");
    }

    [Fact]
    public void AToolWithoutAnOutputGroupOnlyStatesTheExitCode()
    {
        ToolRunMessage.Finished("My tool", 3, outputGroup: null)
            .Should().Be("My tool finished (exit code 3).");
    }

    [Fact]
    public void AToolThatReportsNoSummaryJustFinished()
    {
        ToolRunMessage.Finished("LOOT", exitCode: null, outputGroup: null).Should().Be("LOOT finished.");
    }
}
