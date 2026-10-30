using FluentAssertions;
using SilverModManager.Sdk.ProxyConsole;
using Spectre.Console.Testing;

namespace SilverModManager.ProxyConsole.Tests;

public class ProgressRenderTests
{
    [Fact]
    public async Task ProgressTaskBeforeStartProgress_NamesTheMissingStart()
    {
        var renderer = new SpectreRenderer(new TestConsole());

        var act = async () => await renderer.RenderAsync(new CreateProgressTask { Text = "Working", TaskId = Guid.NewGuid() });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*StartProgress*");
    }

    [Fact]
    public async Task StopProgressBeforeStartProgress_NamesTheMissingStart()
    {
        var renderer = new SpectreRenderer(new TestConsole());

        var act = async () => await renderer.RenderAsync(new StopProgress());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*StartProgress*");
    }
}
