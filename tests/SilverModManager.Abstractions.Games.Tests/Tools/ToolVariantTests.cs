using FluentAssertions;
using SilverModManager.Abstractions.Games.Tools;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests.Tools;

public class ToolVariantTests
{
    private static readonly ToolDefinition Editor = new()
    {
        Id = new ToolId("xedit"),
        DisplayName = "xEdit",
        AppliesTo = _ => true,
        ExecutableNames = _ => ["xEdit.exe"],
        Arguments = ArgumentTemplate.Of("-D:{DataFolder}"),
        Output = new ToolOutput.GameFolder("xEdit Output"),
        ExitCodes = ExitCodePolicy.AnyExitIsFine,
    };

    [Fact]
    public void Apply_ReplacesArgumentsAndExitPolicyAndNamesTheVariant()
    {
        var variant = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("{Plugin}"), ExitCodePolicy.ZeroIsSuccess);

        var applied = variant.Apply(Editor with { QuickAutoClean = variant });

        applied.DisplayName.Should().Be("xEdit: Quick Auto Clean");
        applied.Arguments.Words.Should().Equal("{Plugin}");
        applied.ExitCodes.Should().Be(ExitCodePolicy.ZeroIsSuccess);
        applied.QuickAutoClean.Should().BeNull("a variant has no variant of its own");
    }

    [Fact]
    public void Apply_KeepsEverythingElseAboutTheTool()
    {
        var applied = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("{Plugin}"), ExitCodePolicy.ZeroIsSuccess).Apply(Editor);

        applied.Id.Should().Be(Editor.Id);
        applied.Output.Should().Be(Editor.Output);
        applied.AppliesTo.Should().BeSameAs(Editor.AppliesTo);
    }
}
