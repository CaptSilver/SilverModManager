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
    private static (OpenUriFailureService Service, Func<IOverlayViewModel?> Shown, IMarkdownRendererViewModel Markdown) Create()
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
        return (service, () => shown, markdown);
    }

    [Fact]
    public async Task ShowAsync_EnqueuesADialogWhoseCopyableContentsAreTheLink()
    {
        var (service, shown, markdown) = Create();

        var link = new Uri("https://example.com/releases/1");
        await service.ShowAsync(new OpenUriFailure(link, "No browser could be started."));

        shown().Should().BeOfType<MessageBoxOkViewModel>().Which.MarkdownRenderer.Should().BeSameAs(markdown);
        markdown.Contents.Should().Be(link.AbsoluteUri);
    }

    [Fact]
    public async Task ShowAsync_ForAFileUriShowsThePathUnderAPathTitle()
    {
        var (service, shown, markdown) = Create();

        await service.ShowAsync(new OpenUriFailure(new Uri("/home/deck/My Mods/loadout"), "There is no file manager in Game Mode."));

        var dialog = shown().Should().BeOfType<MessageBoxOkViewModel>().Subject;
        dialog.Title.Should().Be("Could not open the path");
        dialog.Description.Should().Be("There is no file manager in Game Mode. Copy it and open it yourself.");
        markdown.Contents.Should().Be("/home/deck/My Mods/loadout");
    }
}
