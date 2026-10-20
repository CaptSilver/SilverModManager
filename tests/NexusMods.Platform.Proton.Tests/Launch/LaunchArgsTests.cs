using FluentAssertions;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Platform.Proton.Tests.Launch;

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
}
