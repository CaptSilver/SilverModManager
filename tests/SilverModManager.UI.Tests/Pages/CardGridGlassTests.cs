using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The store pages' cards sit on the page panel, which Glass tints; an opaque card would hide that tint.</summary>
public class CardGridGlassTests(IServiceProvider provider) : AUiTest(provider)
{
    private static Color? BackgroundColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    [Fact]
    public async Task ACardIsAWashOverThePanelRatherThanAnOpaqueBlock()
    {
        await OnUi(() =>
        {
            var grid = new CardGrid
            {
                CardTemplate = new FuncDataTemplate<string>((_, _) => new Border { Classes = { "GridCard" }, Height = 100 }),
                Items = ["a", "b"],
            };
            var window = Track(new Window { Width = 900, Height = 600, Content = grid });
            ShowRendered(window);
            Render(window);

            var card = grid.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("GridCard"));
            BackgroundColor(card.Background)?.A.Should().BeLessThan(255, "a card at rest lets the page panel show through");

            ((IPseudoClasses)card.Classes).Set(":pointerover", true);
            BackgroundColor(card.Background)?.A.Should().BeLessThan(255, "a hovered card lets the page panel show through");
            BackgroundColor(card.Background).Should().NotBeNull("a card has a background to lift it off the panel");
        });
    }
}
