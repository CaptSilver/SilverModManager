using FluentAssertions;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.CLI.Tests;

public class CliErrorsCommandLineHintTests
{
    private sealed class Refusal(string message, string? hint) : UserFacingException(message)
    {
        public override string Title => "Refused";
        public override string? CommandLineHint => hint;
    }

    [Fact]
    public void ARefusalWithAHint_PrintsTheMessageThenTheHintOnItsOwnParagraph()
        => CliErrors.Describe(new Refusal("Nothing was changed.", "Use --large-files backup."))
            .Should().Be($"Nothing was changed.{Environment.NewLine}{Environment.NewLine}Use --large-files backup.");

    [Fact]
    public void ARefusalWithoutAHint_PrintsJustTheMessage()
        => CliErrors.Describe(new Refusal("Nothing was changed.", null)).Should().Be("Nothing was changed.");

    [Fact]
    public void AHintBehindAWrapper_IsStillPrinted()
        => CliErrors.Describe(new AggregateException(new Refusal("Nothing was changed.", "Try again.")))
            .Should().EndWith("Try again.");

    [Fact]
    public void SeveralRefusals_KeepEachDistinctHint()
    {
        var joined = new AggregateException(new Refusal("First.", "Use --large-files backup."), new Refusal("Second.", "Use --large-files backup."), new Refusal("Third.", "Try again."));

        var text = CliErrors.Describe(joined);

        text.Should().Contain("First.").And.Contain("Second.").And.Contain("Third.");
        text.Split("Use --large-files backup.").Should().HaveCount(2, "the same hint is printed once");
        text.Should().Contain("Try again.");
    }

    [Fact]
    public void SeveralRefusalsWithoutHints_PrintJustTheMessages()
        => CliErrors.Describe(new AggregateException(new Refusal("First.", null), new Refusal("Second.", null)))
            .Should().Be($"First.{Environment.NewLine}Second.");
}
