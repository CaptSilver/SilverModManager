using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Markdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.Sdk;
using NSubstitute;

namespace SilverModManager.UI.Tests.Controls;

public class MarkdownRendererTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Rendered(Window Window, MarkdownScrollViewer Viewer, IAvaloniaInterop Interop);

    private async Task<Rendered> Render(string markdown)
    {
        var interop = Substitute.For<IAvaloniaInterop>();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var viewModel = new MarkdownRendererViewModel(
                Provider,
                Provider.GetRequiredService<ILogger<MarkdownRendererViewModel>>(),
                Substitute.For<IOSInterop>(),
                interop
            ) { Contents = markdown };

            var window = new Window { Width = 800, Height = 600, Content = new MarkdownRendererView { DataContext = viewModel } };
            window.Show();

            var viewer = window.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single();
            return new Rendered(window, viewer, interop);
        });
    }

    private static async Task<List<Button>> CopyButtons(Rendered rendered)
    {
        // The view binds its contents when it is activated, so the blocks appear a beat after Show().
        for (var i = 0; i < 100; i++)
        {
            var buttons = await Dispatcher.UIThread.InvokeAsync(() => rendered.Viewer.GetVisualDescendants().OfType<Button>().ToList());
            if (buttons.Count > 0) return buttons;
            await Task.Delay(50);
        }
        return [];
    }

    private static async Task Press(Button button)
    {
        await Dispatcher.UIThread.InvokeAsync(() => button.Command!.Execute(button.CommandParameter));
    }

    [Fact]
    public async Task Test_TextSelectionIsEnabled()
    {
        var rendered = await Render("Some **diagnostic** text");

        var enabled = await Dispatcher.UIThread.InvokeAsync(() => rendered.Viewer.SelectionEnabled);
        enabled.Should().BeTrue("diagnostic text has to be selectable and copyable");
    }

    [Fact]
    public async Task Test_FencedBlockCopiesItsExactText()
    {
        var rendered = await Render("Before\n\n```\nWINEDLLOVERRIDES=\"winmm=n,b\" %command%\n```\n\nAfter");

        var buttons = await CopyButtons(rendered);
        buttons.Should().ContainSingle();
        await Press(buttons[0]);

        await rendered.Interop.Received(1).SetClipboardTextAsync("WINEDLLOVERRIDES=\"winmm=n,b\" %command%");
    }

    [Fact]
    public async Task Test_FencedBlockWithLanguageKeepsInnerLinesAndDropsTheFences()
    {
        var rendered = await Render("```bash\nfirst line\n\n    indented\nlast line\n```");

        var buttons = await CopyButtons(rendered);
        buttons.Should().ContainSingle();
        await Press(buttons[0]);

        await rendered.Interop.Received(1).SetClipboardTextAsync("first line\n\n    indented\nlast line");
    }

    [Fact]
    public async Task Test_EachBlockCopiesItsOwnText()
    {
        var rendered = await Render("```\none\n```\n\ntext\n\n```\ntwo\n```");

        var buttons = await CopyButtons(rendered);
        buttons.Should().HaveCount(2);
        await Press(buttons[1]);

        await rendered.Interop.Received(1).SetClipboardTextAsync("two");
        await rendered.Interop.DidNotReceive().SetClipboardTextAsync("one");
    }

    [Fact]
    public async Task Test_TextAroundABlockStillRenders()
    {
        var rendered = await Render("Before\n\n```\ncode\n```\n\nAfter");

        await CopyButtons(rendered);
        var text = await Dispatcher.UIThread.InvokeAsync(() => string.Join("|", rendered.Viewer.GetVisualDescendants()
            .OfType<ColorTextBlock.Avalonia.CTextBlock>().Select(t => t.Text)));
        text.Should().Contain("Before").And.Contain("After");
    }

    [Fact]
    public async Task Test_CodeFontStylesTheCodeButNotTheCopyButton()
    {
        var rendered = await Render("```\ncode\n```");

        var buttons = await CopyButtons(rendered);
        buttons.Should().ContainSingle();
        var (codeFont, labelFont) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var code = rendered.Viewer.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == "code");
            var label = buttons[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Copy");
            return (code.FontFamily, label.FontFamily);
        });

        codeFont.Name.Should().Contain("Mono");
        labelFont.Should().NotBe(codeFont,"the code block's monospace style must not leak onto the button label");
    }

    [Fact]
    public async Task Test_UnclosedFenceCopiesTheRestOfTheDocument()
    {
        var rendered = await Render("```\nstill going\nlast");

        var buttons = await CopyButtons(rendered);
        buttons.Should().ContainSingle();
        await Press(buttons[0]);

        await rendered.Interop.Received(1).SetClipboardTextAsync("still going\nlast");
    }

    [Fact]
    public async Task Test_ShorterFenceInsideALongerOneIsPartOfTheCode()
    {
        var rendered = await Render("````\n```\ninner\n```\n````");

        var buttons = await CopyButtons(rendered);
        buttons.Should().ContainSingle();
        await Press(buttons[0]);

        await rendered.Interop.Received(1).SetClipboardTextAsync("```\ninner\n```");
    }
}
