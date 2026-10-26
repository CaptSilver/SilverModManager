using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.Commandline;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod version</c>, run through the command line configurator with only the status verbs registered.
/// </summary>
public class VersionVerbTests
{
    private static async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddStatusVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    [Fact]
    public async Task PrintsTheProductNameVersionAndCommit()
    {
        var (exitCode, output) = await Run("version");

        exitCode.Should().Be(0);
        var commit = ApplicationConstants.CommitHash is null ? "" : $" ({ApplicationConstants.CommitHash})";
        output.Trim().Should().Be($"SilverModManager {ApplicationConstants.Version}{commit}");
    }
}
