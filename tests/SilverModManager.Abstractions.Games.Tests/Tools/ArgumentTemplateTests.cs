using FluentAssertions;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Sdk.Errors;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests.Tools;

public class ArgumentTemplateTests
{
    private static readonly Dictionary<string, string> Variables = new()
    {
        ["DataFolder"] = "/games/Skyrim Special Edition/Data",
        ["LoadoutName"] = "My Loadout",
    };

    [Fact]
    public void Substitute_KeepsAWordWhole_WhenTheValueHoldsSpaces()
    {
        var words = ArgumentTemplate.Of("-D:{DataFolder}", "-autoexit").Substitute(Variables);

        words.Should().Equal("-D:/games/Skyrim Special Edition/Data", "-autoexit");
    }

    [Fact]
    public void Substitute_ReplacesSeveralPlaceholdersInOneWord()
    {
        ArgumentTemplate.Of("{LoadoutName}@{DataFolder}").Substitute(Variables)
            .Should().Equal("My Loadout@/games/Skyrim Special Edition/Data");
    }

    [Fact]
    public void Substitute_MatchesVariableNamesIgnoringCase()
    {
        ArgumentTemplate.Of("{dataFolder}").Substitute(Variables).Should().Equal("/games/Skyrim Special Edition/Data");
    }

    [Fact]
    public void Substitute_NamesTheVariable_WhenItIsUnknown()
    {
        var act = () => ArgumentTemplate.Of("-x", "-O:{OutputFolder}").Substitute(Variables);

        act.Should().Throw<UnknownToolVariableException>()
            .Which.VariableName.Should().Be("OutputFolder");
    }

    [Fact]
    public void Substitute_RefusesAnUnknownVariable_InWordsForThePlayer()
    {
        var act = () => ArgumentTemplate.Of("-O:{OutputFolder}").Substitute(Variables);

        var refusal = act.Should().Throw<UserFacingException>().Which;
        refusal.Title.Should().Be("The tool was not run");
        refusal.Message.Should().Contain("{OutputFolder}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\": 1}")]
    [InlineData("{ not a name }")]
    [InlineData("{unclosed")]
    public void Substitute_LeavesBracesThatAreNotAVariableName(string word)
    {
        ArgumentTemplate.Of(word).Substitute(Variables).Should().Equal(word);
    }

    [Fact]
    public void Of_WithNoWords_IsEmpty()
    {
        ArgumentTemplate.Of().Substitute(Variables).Should().BeEmpty();
        ArgumentTemplate.Empty.Words.Should().BeEmpty();
    }
}
