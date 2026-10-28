using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.CommandLine.Verbs;

namespace SilverModManager.CLI.Tests;

public class BackupOverflowChoicesTests
{
    [Fact]
    public void Parse_WithNoOption_Refuses() => BackupOverflowChoices.Parse(null).Should().Be(BackupOverflowChoice.Refuse);

    [Theory]
    [InlineData("adopt", BackupOverflowChoice.AdoptInPlace)]
    [InlineData(" Adopt ", BackupOverflowChoice.AdoptInPlace)]
    [InlineData("backup", BackupOverflowChoice.BackUpAnyway)]
    [InlineData("BACKUP", BackupOverflowChoice.BackUpAnyway)]
    public void Parse_MapsEachValueToItsChoice(string value, BackupOverflowChoice expected) =>
        BackupOverflowChoices.Parse(value).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("sometimes")]
    [InlineData("refuse")]
    public void Parse_WithAnyOtherValue_Throws(string value)
    {
        var act = () => BackupOverflowChoices.Parse(value);
        act.Should().Throw<InvalidLargeFilesOptionException>();
    }
}
