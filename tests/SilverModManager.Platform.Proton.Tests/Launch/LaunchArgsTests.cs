using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class LaunchArgsTests
{
    private static LaunchArgs Parse(params string[] args)
    {
        LaunchArgs.TryParse(args, out var parsed, out var error).Should().BeTrue(error);
        return parsed!;
    }

    private static string Fail(params string[] args)
    {
        LaunchArgs.TryParse(args, out var parsed, out var error).Should().BeFalse();
        parsed.Should().BeNull();
        return error!;
    }

    [Fact]
    public void HelpTextStartsWithTheSynopsisAndDocumentsEveryOption()
    {
        LaunchArgs.HelpText.Should().StartWith(LaunchArgs.Synopsis);

        // The synopsis already names every option, so only the lines after it show each one is explained.
        var lines = LaunchArgs.HelpText[LaunchArgs.Synopsis.Length..].Split('\n').Select(line => line.Trim()).ToArray();
        lines.Should().Contain(line => line.StartsWith("<loadout> "));
        lines.Should().Contain(line => line.StartsWith("--game "));
        lines.Should().Contain(line => line.StartsWith("--dry-run "));
        lines.Should().Contain(line => line.StartsWith("--secrets-file "));
        lines.Should().Contain(line => line.StartsWith("-- "));
        lines.Should().Contain(line => line.Contains("125"));
    }

    [Fact]
    public void TakesTheNameAndEverythingAfterTheSeparator()
    {
        var parsed = Parse("My Loadout", "--", "/games/game.exe", "-windowed");

        parsed.LoadoutName.Should().Be("My Loadout");
        parsed.Game.Should().BeNull();
        parsed.Command.Should().Equal("/games/game.exe", "-windowed");
    }

    [Fact]
    public void KeepsASecondSeparatorInTheCommand()
    {
        Parse("L", "--", "wine", "--", "game.exe").Command.Should().Equal("wine", "--", "game.exe");
    }

    [Fact]
    public void LeavesGameFlagsAfterTheSeparatorToTheCommand()
    {
        var parsed = Parse("L", "--", "game.exe", "--game", "42");

        parsed.Game.Should().BeNull();
        parsed.Command.Should().Equal("game.exe", "--game", "42");
    }

    [Theory]
    [InlineData("L", "--game", "42", "--")]
    [InlineData("--game", "42", "L", "--")]
    [InlineData("L", "--game=42", "--")]
    public void ReadsTheGameFlagBeforeOrAfterTheName(params string[] head)
    {
        var parsed = Parse([.. head, "game.exe"]);

        parsed.LoadoutName.Should().Be("L");
        parsed.Game.Should().Be("42");
        parsed.Command.Should().Equal("game.exe");
    }

    [Theory]
    [InlineData("L", "--dry-run", "--")]
    [InlineData("--dry-run", "L", "--")]
    public void ReadsTheDryRunFlagBeforeOrAfterTheName(params string[] head)
    {
        var parsed = Parse([.. head, "game.exe"]);

        parsed.DryRun.Should().BeTrue();
        parsed.LoadoutName.Should().Be("L");
        parsed.Command.Should().Equal("game.exe");
    }

    [Fact]
    public void DryRunIsOffByDefault_AndAfterTheSeparatorBelongsToTheCommand()
    {
        var parsed = Parse("L", "--", "game.exe", "--dry-run");

        parsed.DryRun.Should().BeFalse();
        parsed.Command.Should().Equal("game.exe", "--dry-run");
    }

    [Fact]
    public void DryRunTwice_IsAUsageError()
    {
        Fail("L", "--dry-run", "--dry-run", "--", "x").Should().Contain("--dry-run").And.Contain(LaunchArgs.Synopsis);
    }

    [Fact]
    public void KeepsTheNameVerbatim()
    {
        // Matching trims and ignores case later; the parser does not rewrite what Steam passed.
        Parse("  Spaced Name ", "--", "x").LoadoutName.Should().Be("  Spaced Name ");
    }

    [Fact]
    public void MissingName_IsAUsageError()
    {
        Fail("--", "game.exe").Should().Contain(LaunchArgs.Synopsis);
        Fail().Should().Contain(LaunchArgs.Synopsis);
    }

    [Fact]
    public void MissingSeparator_IsAUsageError()
    {
        Fail("L").Should().Contain(LaunchArgs.Synopsis);
        Fail("L", "game.exe").Should().Contain(LaunchArgs.Synopsis);
    }

    [Fact]
    public void EmptyCommand_IsAUsageError()
    {
        Fail("L", "--").Should().Contain(LaunchArgs.Synopsis);
    }

    [Fact]
    public void ReportsMalformedFlags()
    {
        Fail("L", "--game", "--", "x").Should().Contain("--game").And.Contain(LaunchArgs.Synopsis);
        Fail("L", "--game").Should().Contain(LaunchArgs.Synopsis);
        Fail("L", "--game=", "--", "x").Should().Contain("--game needs a value");
        Fail("L", "--game", " ", "--", "x").Should().Contain("--game needs a value");
        Fail("L", "--game", "1", "--game", "2", "--", "x").Should().Contain("--game");
        Fail("L", "--bogus", "--", "x").Should().Contain("--bogus");
        Fail("L", "Other", "--", "x").Should().Contain("Other");
    }

    [Theory]
    [InlineData("L", "--secrets-file", "/tmp/secrets.env", "--")]
    [InlineData("--secrets-file", "/tmp/secrets.env", "L", "--")]
    [InlineData("L", "--secrets-file=/tmp/secrets.env", "--")]
    public void ReadsTheSecretsFileBeforeOrAfterTheName(params string[] head)
    {
        var parsed = Parse([.. head, "game.exe"]);

        parsed.SecretsFile.Should().NotBeNull();
        parsed.SecretsFile!.Value.GetFullPath().Should().Be("/tmp/secrets.env");
        parsed.LoadoutName.Should().Be("L");
        parsed.Command.Should().Equal("game.exe");
    }

    [Fact]
    public void ARelativeSecretsFileIsResolvedAgainstTheWorkingDirectory()
    {
        var parsed = Parse("L", "--secrets-file", "secrets.env", "--", "x");

        parsed.SecretsFile!.Value.GetFullPath().Should().Be(Path.GetFullPath("secrets.env"));
    }

    [Fact]
    public void ThereIsNoSecretsFileUnlessOneIsGiven()
    {
        Parse("L", "--", "x").SecretsFile.Should().BeNull();
    }

    [Fact]
    public void SecretsFileAfterTheSeparatorBelongsToTheCommand()
    {
        var parsed = Parse("L", "--", "game.exe", "--secrets-file", "/tmp/x");

        parsed.SecretsFile.Should().BeNull();
        parsed.Command.Should().Equal("game.exe", "--secrets-file", "/tmp/x");
    }

    [Fact]
    public void ReportsMalformedSecretsFileFlags()
    {
        Fail("L", "--secrets-file", "--", "x").Should().Contain("--secrets-file").And.Contain(LaunchArgs.Synopsis);
        Fail("L", "--secrets-file").Should().Contain(LaunchArgs.Synopsis);
        Fail("L", "--secrets-file=", "--", "x").Should().Contain("--secrets-file needs a value");
        Fail("L", "--secrets-file", " ", "--", "x").Should().Contain("--secrets-file needs a value");
        Fail("L", "--secrets-file", "/a", "--secrets-file=/b", "--", "x").Should().Contain("--secrets-file given more than once");
    }

    [Fact]
    public void TheSynopsisNamesTheSecretsFile()
    {
        LaunchArgs.Synopsis.Should().Contain("[--secrets-file <path>]");
    }
}
