using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.DataModel.CommandLine.Verbs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod games build-covered</c> against a registry that returns made-up installations and a file hashes
/// service that says which locator ids it covers.
/// </summary>
public class GamesBuildCoveredVerbTests
{
    private static readonly GameId Covered = GameId.From("BuildCovered.Game");

    private static GameInstallation Install(GameId id, params string[] locatorIds)
    {
        var game = new StubGameData(id.ToString(), id);
        return new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/" + id),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..locatorIds.Select(LocatorId.From)],
            Locator = Substitute.For<IGameLocator>(),
        }, default!);
    }

    private static async Task<(int ExitCode, string Output)> Run(
        ImmutableArray<GameInstallation> installs, bool[] covered, params string[] args)
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(installs);
        var hashes = Substitute.For<IFileHashesService>();
        // The verb builds a fresh id array per call, and arrays compare by reference, so answer by the ids' values.
        hashes.CoversInstalledBuild(default).ReturnsForAnyArgs(call =>
        {
            var ids = call.Arg<LocatorIdsWithGameStore>().LocatorIds.Select(i => i.Value);
            var index = installs.ToList().FindIndex(g => g.LocatorResult.LocatorIds.Select(i => i.Value).SequenceEqual(ids));
            return covered[index];
        });

        var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddSingleton(registry)
            .AddSingleton(hashes)
            .AddGameVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    [Fact]
    public async Task ACoveredBuildPrintsTrueAndItsLocatorIds()
    {
        var (exit, output) = await Run([Install(Covered, "111", "222")], [true],
            "games", "build-covered", "-g", "BuildCovered.Game");

        exit.Should().Be(0);
        output.Should().Contain("true").And.Contain("111").And.Contain("222");
    }

    [Fact]
    public async Task AnUncoveredBuildPrintsFalseAndExitsOne()
    {
        var (exit, output) = await Run([Install(Covered, "111")], [false],
            "games", "build-covered", "-g", "BuildCovered.Game");

        exit.Should().Be(1);
        output.Should().Contain("false").And.NotContain("true");
    }

    [Fact]
    public async Task ASecondInstallationThatIsUncoveredFailsTheRun()
    {
        var (exit, output) = await Run([Install(Covered, "111"), Install(Covered, "999")], [true, false],
            "games", "build-covered", "-g", "BuildCovered.Game");

        exit.Should().Be(1);
        output.Should().Contain("true").And.Contain("false");
    }

    [Fact]
    public async Task AGameThatIsNotLocatedSaysSoAndExitsOne()
    {
        var (exit, output) = await Run([Install(Covered, "111")], [true],
            "games", "build-covered", "-g", "Not.Installed");

        exit.Should().Be(1);
        output.Should().Contain("Not.Installed").And.Contain("games list");
    }
}
