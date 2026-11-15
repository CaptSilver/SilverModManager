using FluentAssertions;
using SilverModManager.Sdk.Ini;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>The part of the legacy tweak migration that needs no database: which lines of a tweak the list already sets.</summary>
public class LegacyIniTweaksTests
{
    private static string Without(string document, string list)
        => LegacyIniTweaks.WithoutLinesSetByTheList(IniDocument.Parse(document), IniDocument.Parse(list)).ToText();

    [Fact]
    public void WithoutLinesSetByTheList_DropsEveryCopyOfAKeyTheListSetsToTheSameValue()
    {
        var result = Without("[Display]\nfMaxTime=0.1\nfMaxTime=0.1\niSize=2\n", "[Display]\nfMaxTime=0.1\n");

        result.Should().Be("[Display]\niSize=2\n");
    }

    [Fact]
    public void WithoutLinesSetByTheList_KeepsAKeyTheListSetsToAnotherValue()
    {
        var result = Without("[Display]\nfMaxTime=0.2\n", "[Display]\nfMaxTime=0.1\n");

        result.Should().Be("[Display]\nfMaxTime=0.2\n");
    }

    [Fact]
    public void WithoutLinesSetByTheList_MatchesSectionAndKeyIgnoringCase()
    {
        var result = Without("[display]\nFMAXTIME=0.1\n[Other]\nfMaxTime=0.1\n", "[Display]\nfMaxTime=0.1\n");

        result.Should().Be("[display]\n[Other]\nfMaxTime=0.1\n");
    }

    [Fact]
    public void WithoutLinesSetByTheList_KeepsCommentsAndOtherSections()
    {
        var result = Without("; mine\n[Display]\nfMaxTime=0.1\n[General]\nsLanguage=EN\n", "[Display]\nfMaxTime=0.1\n");

        result.Should().Be("; mine\n[Display]\n[General]\nsLanguage=EN\n");
    }
}
