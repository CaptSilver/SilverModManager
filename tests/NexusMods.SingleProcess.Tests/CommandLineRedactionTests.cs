using FluentAssertions;

namespace NexusMods.SingleProcess.Tests;

public class CommandLineRedactionTests
{
    [Fact]
    public void KeepsTheVerbNameAndOptionNamesAndReplacesValues()
    {
        CommandLineConfigurator.Redact(["nexus-api-key", "--game", "stardewvalley"])
            .Should().Be("nexus-api-key --game …");
    }

    [Fact]
    public void ReplacesPositionalArguments()
    {
        CommandLineConfigurator.Redact(["run", "my-loadout", "--dry-run"])
            .Should().Be("run … --dry-run");
    }

    [Fact]
    public void KeepsTheOptionNameOfAnEqualsForm()
    {
        CommandLineConfigurator.Redact(["run", "--secrets-file=/home/me/secrets.env"])
            .Should().Be("run --secrets-file=…");
    }

    [Fact]
    public void ReplacesEverythingAfterTheSeparator()
    {
        CommandLineConfigurator.Redact(["run", "loadout", "--", "game.exe", "--token", "abc"])
            .Should().Be("run … -- … … …");
    }

    [Fact]
    public void NeverContainsAValue()
    {
        var line = CommandLineConfigurator.Redact(["nexus-api-key", "--apikey", "hunter2", "hunter3"]);
        line.Should().NotContain("hunter");
    }

    [Fact]
    public void NoArgumentsGiveAnEmptyLine()
    {
        CommandLineConfigurator.Redact([]).Should().BeEmpty();
    }
}
