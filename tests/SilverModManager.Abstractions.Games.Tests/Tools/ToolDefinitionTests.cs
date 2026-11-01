using FluentAssertions;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Sdk.Games;
using NSubstitute;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests.Tools;

public class ToolDefinitionTests
{
    private static ToolDefinition Minimal => new()
    {
        DisplayName = "Some Tool",
        AppliesTo = _ => true,
        ExecutableNames = _ => ["some.exe"],
        Arguments = ArgumentTemplate.Empty,
    };

    [Fact]
    public void Defaults_DescribeAnAdHocWindowsProgramThatMustExitCleanly()
    {
        var definition = Minimal;

        definition.Id.Should().BeNull();
        definition.RunVia.Should().Be(ToolRunVia.Auto);
        definition.ExitCodes.Should().Be(ExitCodePolicy.ZeroIsSuccess);
        definition.Output.Should().Be(ToolOutput.None);
        definition.Prefix.Should().Be(ToolPrefixRequirements.None);
        definition.IgnoredOutput.Should().BeEmpty();
        definition.ExtraArguments.Should().BeNull();
    }

    [Fact]
    public void AppliesTo_IsAPredicateOverTheGame()
    {
        var wanted = Substitute.For<IGame>();
        wanted.DisplayName.Returns("Wanted");
        var other = Substitute.For<IGame>();
        other.DisplayName.Returns("Other");
        var definition = Minimal with { AppliesTo = game => game.DisplayName == "Wanted" };

        definition.AppliesTo(wanted).Should().BeTrue();
        definition.AppliesTo(other).Should().BeFalse();
    }

    [Fact]
    public void ExecutableNames_CanDependOnTheGame()
    {
        var game = Substitute.For<IGame>();
        game.DisplayName.Returns("Skyrim");
        var definition = Minimal with { ExecutableNames = g => [$"{g.DisplayName}Edit.exe"] };

        definition.ExecutableNames(game).Should().Equal("SkyrimEdit.exe");
    }

    [Fact]
    public void ToolOutput_NamesTheGroupAndWhereItsFilesGo()
    {
        var folder = new ToolOutput.GameFolder("xEdit Output");
        var declared = new ToolOutput.DeclaredFolder("DynDOLOD Output", new GamePath(LocationId.Game, "Data"));

        folder.GroupName.Should().Be("xEdit Output");
        declared.MapsTo.Should().Be(new GamePath(LocationId.Game, "Data"));
    }

    [Fact]
    public void ToolId_IsItsText()
    {
        new ToolId("xedit").ToString().Should().Be("xedit");
    }
}
