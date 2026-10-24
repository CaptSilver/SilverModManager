using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests.Overlays;

public class OpenUriFailureServiceTests
{
    [Fact]
    public async Task ShowAsync_EnqueuesADialogWhoseCopyableContentsAreTheLink()
    {
        var overlays = Substitute.For<IOverlayController>();
        var markdown = Substitute.For<IMarkdownRendererViewModel>();
        IOverlayViewModel? shown = null;
        overlays.EnqueueAndWait(Arg.Any<MessageBoxOkViewModel>()).Returns(call =>
        {
            shown = call.Arg<IOverlayViewModel>();
            return Task.FromResult<R3.Unit>(default);
        });

        var services = new ServiceCollection()
            .AddSingleton(overlays)
            .AddSingleton(markdown)
            .BuildServiceProvider();
        var service = new OpenUriFailureService(Substitute.For<IOSInterop>(), services, NullLogger<OpenUriFailureService>.Instance);

        var link = new Uri("https://example.com/releases/1");
        await service.ShowAsync(link);

        shown.Should().BeOfType<MessageBoxOkViewModel>().Which.MarkdownRenderer.Should().BeSameAs(markdown);
        markdown.Contents.Should().Be(link.AbsoluteUri);
    }
}
