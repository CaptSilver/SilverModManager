using System.Reflection;
using FluentAssertions;
using Projektanker.Icons.Avalonia.MaterialDesign;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The large pictograms are Material Design glyphs, which take the theme's foreground colour. An
/// <see cref="AvaloniaSvg"/> would carry its own fixed colours, which is how the orange Nexus art got in.
/// </summary>
public class PictogramIconTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IEnumerable<(string Name, IconValue Value)> Pictograms() => typeof(IconValues)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.Name.StartsWith("Pictogram", StringComparison.Ordinal) && field.FieldType == typeof(IconValue))
        .Select(field => (field.Name, (IconValue)field.GetValue(null)!));

    [Fact]
    public void Test_PictogramsExist()
    {
        Pictograms().Should().NotBeEmpty("the reflection filter must match the Pictogram* fields");
    }

    [Fact]
    public void Test_EveryPictogramIsAThemedGlyph()
    {
        var wrong = Pictograms()
            .Where(p => !(p.Value.Value.IsT1 || p.Value.Value.IsT5))
            .Select(p => $"{p.Name} is {p.Value.Value.Value.GetType().Name}");

        wrong.Should().BeEmpty("a ProjektankerIcon or SimpleVectorIcon takes the theme colour, an AvaloniaSvg keeps its own");
    }

    [Fact]
    public void Test_EveryPictogramGlyphExistsInTheIconSet()
    {
        var provider = new MaterialDesignIconProvider();
        var missing = Pictograms()
            .Where(p => p.Value.Value.IsT1)
            .Select(p => (p.Name, Glyph: p.Value.Value.AsT1.Value ?? ""))
            .Where(p => !Resolves(provider, p.Glyph))
            .Select(p => $"{p.Name} uses {p.Glyph}");

        missing.Should().BeEmpty("a glyph name the icon set does not know renders as nothing");
    }

    private static bool Resolves(MaterialDesignIconProvider provider, string glyph)
    {
        try
        {
            return provider.GetIcon(glyph) is not null;
        }
        catch (KeyNotFoundException)
        {
            // The provider throws this for a name it has no path for; that is the answer here.
            return false;
        }
    }
}
