using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.DataModel.CommandLine.Verbs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod games list</c> against a registry that returns made-up installations.
/// </summary>
public class GamesListVerbTests
{
    private static GameInstallation Install(string name, GameStore store, string path)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        return new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath(path),
            Store = store,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        }, default!);
    }

    private static async Task<(int ExitCode, LoggingRenderer Renderer)> RunRaw(ImmutableArray<GameInstallation> installs, params string[] args)
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(installs);

        var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddSingleton(registry)
            .AddGameVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static async Task<(int ExitCode, string Output)> Run(ImmutableArray<GameInstallation> installs, params string[] args)
    {
        var (exitCode, renderer) = await RunRaw(installs, args);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    private static readonly ImmutableArray<GameInstallation> TwoGames =
    [
        Install("Elden Ring", GameStore.Steam, "/games/EldenRing"),
        Install("Cyberpunk 2077", GameStore.GOG, "/games/Cyberpunk"),
    ];

    [Fact]
    public async Task ListsStoreNameAndPathAsATableRowPerGame()
    {
        var (exitCode, renderer) = await RunRaw(TwoGames, "games", "list");

        exitCode.Should().Be(0);
        renderer.LastTableColumns.Should().Equal("Store", "Name", "Path");
        renderer.LastTable.Rows
            .Select(row => string.Join('|', row.OfType<Text>().Select(t => t.Template)))
            .Should().Equal("Steam|Elden Ring|/games/EldenRing", "GOG|Cyberpunk 2077|/games/Cyberpunk");
    }

    [Fact]
    public async Task JsonIsAnArrayWithStoreNameAndPath()
    {
        var (exitCode, output) = await Run(TwoGames, "games", "list", "--json");

        exitCode.Should().Be(0);
        using var doc = JsonDocument.Parse(output);
        var rows = doc.RootElement.EnumerateArray().ToArray();
        rows.Should().HaveCount(2);
        rows[0].GetProperty("store").GetString().Should().Be("Steam");
        rows[0].GetProperty("name").GetString().Should().Be("Elden Ring");
        rows[0].GetProperty("path").GetString().Should().Be("/games/EldenRing");
    }

    [Fact]
    public async Task JsonPrintsAnApostropheLiterally()
    {
        var (exitCode, output) = await Run([Install("Baldur's Gate 3", GameStore.Steam, "/games/BG3")], "games", "list", "--json");

        exitCode.Should().Be(0);
        output.Should().Contain("Baldur's Gate 3").And.NotContain("\\u0027");
    }

    [Fact]
    public async Task NoGamesSaysSoInsteadOfPrintingNothing()
    {
        var (exitCode, output) = await Run([], "games", "list");

        exitCode.Should().Be(0);
        output.Trim().Should().Be("No games were found.");
    }

    [Fact]
    public async Task NoGamesIsAnEmptyJsonArray()
    {
        var (exitCode, output) = await Run([], "games", "list", "--json");

        exitCode.Should().Be(0);
        JsonDocument.Parse(output).RootElement.GetArrayLength().Should().Be(0);
    }
}
