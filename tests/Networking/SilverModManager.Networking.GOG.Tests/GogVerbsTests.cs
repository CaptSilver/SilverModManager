using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.GOG;
using SilverModManager.Networking.GOG.CLI;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Networking.GOG.Tests;

public class GogVerbsTests
{
    private readonly IClient _client = Substitute.For<IClient>();
    private readonly RecordingRenderer _renderer = new();

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is not Text text) return ValueTask.CompletedTask;

            var line = text.Template;
            for (var i = 0; i < text.Arguments.Length; i++) line = line.Replace($"{{{i}}}", text.Arguments[i]);
            Lines.AddRange(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LogoutRemovesTheLoginAndSaysSo()
    {
        var exitCode = await Verbs.LogoutAsync(_client, _renderer);

        exitCode.Should().Be(0);
        await _client.Received(1).LogOut();
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("removed");
    }

    [Fact]
    public async Task LogoutThatCannotRemoveTheLoginReportsWhyAndFails()
    {
        _client.LogOut().Returns(Task.FromException(new InvalidOperationException("The GOG login cannot be removed: GOG_TOKEN supplies it.")));

        var exitCode = await Verbs.LogoutAsync(_client, _renderer);

        exitCode.Should().Be(1);
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("GOG_TOKEN supplies it");
    }
}
