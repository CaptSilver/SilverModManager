using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.CommandLine.Verbs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary><c>list-tools</c> against doubled tools, as a table and as JSON.</summary>
public class ToolVerbTests
{
    private static ITool Tool(string name, params string[] gameIds)
    {
        var tool = Substitute.For<ITool>();
        tool.Name.Returns(name);
        tool.GameIds.Returns(gameIds.Select(GameId.From).ToArray());
        return tool;
    }

    private static async Task<(int ExitCode, LoggingRenderer Renderer)> Run(params string[] args)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddSingleton(Tool("Run Game", "Test.One", "Test.Two"))
            .AddSingleton(Tool("Run Script Extender", "Test.Two"))
            .AddToolVerbs()
            .BuildServiceProvider();
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    [Fact]
    public async Task ListsEachToolWithItsGamesAsATable()
    {
        var (exitCode, renderer) = await Run("list-tools");

        exitCode.Should().Be(0);
        renderer.LastTableColumns.Should().Equal("Name", "Description");
        renderer.LastTable.Rows.Select(row => string.Join('|', row.OfType<Text>().Select(t => t.Template)))
            .Should().Equal("Run Game|Test.One, Test.Two", "Run Script Extender|Test.Two");
    }

    [Fact]
    public async Task Json_CarriesEachToolsNameAndGameIds()
    {
        var (exitCode, renderer) = await Run("list-tools", "--json");

        exitCode.Should().Be(0);
        var rows = JsonNode.Parse(string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)))!.AsArray().Select(n => n!.AsObject()).ToArray();
        rows.Select(r => (string?)r["name"]).Should().Equal("Run Game", "Run Script Extender");
        rows[0]["gameIds"]!.AsArray().Select(n => (string?)n).Should().Equal("Test.One", "Test.Two");
        rows[1]["gameIds"]!.AsArray().Select(n => (string?)n).Should().Equal("Test.Two");
    }
}
