using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using Size = NexusMods.Paths.Size;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI;
using SilverModManager.Sdk;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Pages.MyGames;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Draws the question in the real dialog window on the headless platform, since the wording tests cannot see a
/// trimmed title or a list that collapsed into one line.
/// </summary>
public class BackupOverflowDialogRenderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static BackupOverflow Witcher() => new(
        Adoptable: Size.GB * 45, AdoptableCount: 95, MustBackUp: Size.Zero, MustBackUpCount: 0, Limit: Size.GB * 5, Operation: BackupOverflowOperation.Launch,
        Groups:
        [
            new("content/content0", 20, Size.GB * 30, BackupOverflowKind.GameFileChanged),
            new("content/patch1", 60, Size.From(13_421_772_800), BackupOverflowKind.GameFileChanged),
            new("bin/x64", 15, Size.From(2_684_354_560), BackupOverflowKind.GameFileChanged),
        ],
        Examples: [new("content/content0/bundles/xml.bundle", Size.From(4_509_715_660), BackupOverflowKind.GameFileChanged)],
        ByKind: new Dictionary<BackupOverflowKind, (int Count, Size Size)> { [BackupOverflowKind.GameFileChanged] = (95, Size.GB * 45) });

    private static string TextOf(TextBlock block)
        => string.IsNullOrEmpty(block.Text) ? InlinesText(block.Inlines) : block.Text;

    private static string InlinesText(IEnumerable<Inline>? inlines)
        => string.Concat(inlines?.Select(i => i switch { Run run => run.Text, Span span => InlinesText(span.Inlines), _ => "" }) ?? []);

    /// <summary>The markdown renderer's text control is not an Avalonia TextBlock, so its inlines are read by name.</summary>
    private static string TextOfAny(Visual visual) => visual switch
    {
        TextBlock block => TextOf(block),
        _ when visual.GetType().GetProperty("Text")?.GetValue(visual) is string { Length: > 0 } text => text,
        _ => InlinesText(visual.GetType().GetProperty("Content")?.GetValue(visual) as IEnumerable<Inline>),
    };

    private static double YOf(Visual? visual, Visual relativeTo)
        => visual?.TranslatePoint(new Point(0, 0), relativeTo)?.Y ?? throw new InvalidOperationException("The control is not on screen");

    private async Task<DialogWindow> Show(BackupOverflowPrompt prompt)
    {
        var renderer = new MarkdownRendererViewModel(Provider, Provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MarkdownRendererViewModel>>(),
            NSubstitute.Substitute.For<IOSInterop>(), NSubstitute.Substitute.For<IAvaloniaInterop>()) { Contents = prompt.Markdown };
        var dialog = DialogFactory.CreateStandardDialog(prompt.Title, new StandardDialogParameters { Markdown = renderer }, prompt.Buttons);

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new DialogWindow { DataContext = dialog.ViewModel, Title = dialog.ViewModel.WindowTitle, Width = 480, SizeToContent = SizeToContent.Height, MaxHeight = 540 });
            ShowRendered(window);
            return window;
        });
    }

    [Fact]
    public async Task LaunchQuestion_ShowsAWholeTitleAListOfFoldersAndAllThreeButtons()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3: Wild Hunt", Witcher());
        var window = await Show(prompt);

        List<double> folderRows = [];
        for (var attempt = 0; attempt < 100 && folderRows.Count < 3; attempt++)
        {
            folderRows = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Render(window);
                return new[] { "content/content0", "content/patch1", "bin/x64" }
                    .Select(folder => window.GetVisualDescendants().OfType<Control>().FirstOrDefault(t => TextOfAny(t).Contains(folder + " —")))
                    .Where(t => t is not null)
                    .Select(t => YOf(t, window))
                    .ToList();
            });
            if (folderRows.Count < 3) await Task.Delay(50);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TitleTextBlock");
            TextOf(title).Should().Be(prompt.Title);
            title.TextLayout.TextLines.Should().NotContain(line => line.HasCollapsed, "the title must fit the dialog without being cut off");

            var dump = string.Join(" | ", window.GetVisualDescendants().Select(TextOfAny).Where(t => t.Length > 0));
            folderRows.Should().HaveCount(3, "each folder gets its own row; tree: " + dump);
            folderRows.Distinct().Should().HaveCount(3, "the folders are a list, not one run of text");

            var buttons = (window.FindControl<Panel>("ButtonsFlexPanel") ?? throw new InvalidOperationException("No button row")).Children.OfType<StandardButton>().ToList();
            buttons.Select(b => b.Text).Should().Equal("Keep them where they are", "Back up 45.0 GB", "Don't start the game");
            buttons.Should().OnlyContain(b => b.Bounds.Width > 0 && YOf(b, window) < window.Bounds.Height, "every button is on screen");
            buttons.Where(b => b.IsDefault).Select(b => b.Text).Should().Equal("Keep them where they are");
        });
    }
}
