using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Games.FileHashes.VerbImpls;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;
using NSubstitute;

namespace SilverModManager.Games.FileHashes.Tests;

public sealed class BuildHashesDbTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-hashes-db-" + Guid.NewGuid().ToString("N"));
    private readonly List<Text> _lines = [];
    private BuildHashesDb? _builder;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "contrib"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_builder is not null) await _builder.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddVersions_GameWithoutNexusId_IsSkippedAndSaysSo()
    {
        File.WriteAllText(Path.Combine(_root, "contrib", "version_aliases.yaml"), """
            Game Without Nexus Id:
              Windows:
                - Name: 1.0.0
                  Steam: ["1"]
            """);
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Game Without Nexus Id");
        game.NexusModsGameId.Returns(Optional<NexusModsGameId>.None);

        var renderer = Substitute.For<IRenderer>();
        renderer.RenderAsync(Arg.Any<IRenderable>()).Returns(call =>
        {
            if (call.Arg<IRenderable>() is Text text) _lines.Add(text);
            return ValueTask.CompletedTask;
        });
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new System.Text.Json.JsonSerializerOptions())
            .AddSingleton(game)
            .BuildServiceProvider();
        _builder = new BuildHashesDb(renderer, provider, new TemporaryFileManager(FileSystem.Shared, FileSystem.Shared.FromUnsanitizedFullPath(_root) / "tmp"), Substitute.For<IGameRegistry>());

        await _builder.AddVersions(FileSystem.Shared.FromUnsanitizedFullPath(_root));

        _lines.Should().Contain(t => t.Template.Contains("no Nexus Mods id") && t.Arguments.Contains("Game Without Nexus Id"));
    }
}
