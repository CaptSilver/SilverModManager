using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Markdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.Collections;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

public class CollectionInstructionsViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private MarkdownRendererViewModel CreateRenderer(string markdown) => new(
        Provider,
        Provider.GetRequiredService<ILogger<MarkdownRendererViewModel>>(),
        Substitute.For<IOSInterop>(),
        Substitute.For<IAvaloniaInterop>()
    ) { Contents = markdown };

    private async Task<CollectionInstructionsView> Show(Func<CollectionInstructions> create)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CollectionInstructionsView { Instructions = create() };
            Track(new Window { Width = 800, Height = 600, Content = view }).Show();
            return view;
        });
    }

    private static Control Section(CollectionInstructionsView view, string name) =>
        view.FindControl<Control>(name) ?? throw new InvalidOperationException($"No control `{name}`");

    [Fact]
    public void NoInstructionsIsEmpty()
    {
        CollectionInstructions.None.HasAny.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task AnyKindOfInstructionsCounts(bool collection, bool required, bool optional)
    {
        var instructions = await Dispatcher.UIThread.InvokeAsync(() => new CollectionInstructions(
            collection ? CreateRenderer("Read me") : null,
            required ? [new ModInstructions("A", "x", CollectionDownloader.ItemType.Required)] : [],
            optional ? [new ModInstructions("B", "y", CollectionDownloader.ItemType.Optional)] : []
        ));

        instructions.HasAny.Should().BeTrue();
    }

    private static CollectionRoot Collection(string installInstructions, params (string Name, string Instructions, bool Optional)[] mods) => new()
    {
        Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("cyberpunk2077"), InstallInstructions = installInstructions },
        Mods = mods.Select(static mod => new Mod
        {
            Name = mod.Name,
            Instructions = mod.Instructions,
            Optional = mod.Optional,
            DomainName = GameDomain.From("cyberpunk2077"),
            Source = new ModSource { Type = ModSourceType.Direct },
        }).ToArray(),
    };

    [Fact]
    public void ReadsTheCollectionTextAndSplitsModNotesByRequiredAndOptional()
    {
        var instructions = CollectionInstructions.From(
            Collection("Install the script extender first", ("Core mod", "Apply the patch", false), ("Extra mod", "Pick 1080p", true)),
            static text => new MarkdownRendererViewModel { Contents = text });

        instructions.Collection.Should().BeAssignableTo<IMarkdownRendererViewModel>()
            .Which.Contents.Should().Be("Install the script extender first");
        instructions.RequiredMods.Should().Equal(new ModInstructions("Core mod", "Apply the patch", CollectionDownloader.ItemType.Required));
        instructions.OptionalMods.Should().Equal(new ModInstructions("Extra mod", "Pick 1080p", CollectionDownloader.ItemType.Optional));
    }

    /// <summary>
    /// Authors write these in a plain text box, one note per line; markdown would run single lines together.
    /// </summary>
    [Fact]
    public void KeepsTheAuthorsLineBreaks()
    {
        var instructions = CollectionInstructions.From(
            Collection("First note\r\nSecond note\n\nLast paragraph"),
            static text => new MarkdownRendererViewModel { Contents = text });

        instructions.Collection!.Contents.Should().Be("First note  \nSecond note\n\nLast paragraph");
    }

    [Fact]
    public void BlankTextIsNoInstructions()
    {
        var instructions = CollectionInstructions.From(
            Collection("  ", ("Core mod", "", false), ("Extra mod", " \n ", true)),
            static _ => throw new InvalidOperationException("no renderer for a blank text"));

        instructions.HasAny.Should().BeFalse();
    }

    [Fact]
    public void ModNotesAloneCountAsInstructions()
    {
        var instructions = CollectionInstructions.From(
            Collection("", ("Core mod", "Apply the patch", false)),
            static _ => throw new InvalidOperationException("no renderer for a blank text"));

        instructions.Collection.Should().BeNull();
        instructions.HasAny.Should().BeTrue();
    }

    [Fact]
    public async Task ShowsTheCollectionTextAndEachModsInstructions()
    {
        var view = await Show(() => new CollectionInstructions(
            CreateRenderer("Install the **script extender** first"),
            [new ModInstructions("Core mod", "Needs the patch applied", CollectionDownloader.ItemType.Required)],
            [new ModInstructions("Extra mod", "Pick the 1080p variant", CollectionDownloader.ItemType.Optional)]
        ));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Section(view, "CollectionInstructionsSection").IsVisible.Should().BeTrue();
            Section(view, "RequiredModsInstructionsSection").IsVisible.Should().BeTrue();
            Section(view, "OptionalModsInstructionsSection").IsVisible.Should().BeTrue();

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            texts.Should().Contain("Core mod").And.Contain("Needs the patch applied");
            texts.Should().Contain("Extra mod").And.Contain("Pick the 1080p variant");
        });

        MarkdownScrollViewer? viewer = null;
        for (var i = 0; i < 100 && viewer is null; i++)
        {
            viewer = await Dispatcher.UIThread.InvokeAsync(() => view.GetVisualDescendants().OfType<MarkdownScrollViewer>().FirstOrDefault());
            if (viewer is null) await Task.Delay(50);
        }
        viewer.Should().NotBeNull("the collection's own text goes through the markdown renderer");
    }

    /// <summary>
    /// The instructions are the whole tab, so there is nothing to fold away.
    /// </summary>
    [Fact]
    public async Task ShowsSectionsWithoutExpanders()
    {
        var view = await Show(() => new CollectionInstructions(
            CreateRenderer("Read me"),
            [new ModInstructions("Core mod", "Needs the patch applied", CollectionDownloader.ItemType.Required)],
            [new ModInstructions("Extra mod", "Pick the 1080p variant", CollectionDownloader.ItemType.Optional)]
        ));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.GetVisualDescendants().OfType<Expander>().Should().BeEmpty();
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            texts.Should().Contain(["Collection Instructions", "Mod Instructions", "Optional Mod Instructions"]);
        });
    }

    [Fact]
    public async Task HidesTheSectionsTheCollectionLacks()
    {
        var view = await Show(() => new CollectionInstructions(
            null,
            [new ModInstructions("Core mod", "Needs the patch applied", CollectionDownloader.ItemType.Required)],
            []
        ));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Section(view, "CollectionInstructionsSection").IsVisible.Should().BeFalse();
            Section(view, "RequiredModsInstructionsSection").IsVisible.Should().BeTrue();
            Section(view, "OptionalModsInstructionsSection").IsVisible.Should().BeFalse();
        });
    }
}
