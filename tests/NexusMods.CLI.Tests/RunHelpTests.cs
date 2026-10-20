using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.SingleProcess;

namespace NexusMods.CLI.Tests;

public class RunHelpTests(IServiceProvider provider)
{
    [Fact]
    public async Task RootHelp_ListsRunBesideTheVerbs()
    {
        var renderer = new LoggingRenderer();

        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["--help"], renderer, CancellationToken.None);

        exitCode.Should().Be(0);
        var help = string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));
        help.Should().MatchRegex(@"(?m)^\s+run\s");
        help.Should().Contain("loadouts");
    }

    [Fact]
    public async Task RootHelp_ListsStatus()
    {
        var renderer = new LoggingRenderer();

        await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["--help"], renderer, CancellationToken.None);

        string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)).Should().MatchRegex(@"(?m)^\s+status\s");
    }

    [Fact]
    public async Task RunHelp_ShowsTheDryRunOption()
    {
        var renderer = new LoggingRenderer();

        await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["run", "--help"], renderer, CancellationToken.None);

        string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)).Should().Contain("--dry-run");
    }

    [Fact]
    public async Task RunHelp_ShowsTheSameSynopsisAsTheArgumentParser()
    {
        var renderer = new LoggingRenderer();

        await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["run", "--help"], renderer, CancellationToken.None);

        var help = string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));
        help.Should().Contain(LaunchArgs.Synopsis["usage: silvermod ".Length..]);
    }

    [Fact]
    public async Task RunThroughTheVerbParser_FailsWithTheSynopsisInsteadOfDoingNothing()
    {
        var renderer = new LoggingRenderer();

        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["run", "Some Loadout", "--", "game.exe"], renderer, CancellationToken.None);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)).Should().Contain(LaunchArgs.Synopsis);
    }
}
