using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests;

/// <summary>
/// A mod, group or loadout name written into a diagnostic's Markdown must not become a link; the plain text form keeps
/// the name exactly as it is.
/// </summary>
public class DiagnosticFormatterEscapeTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Hostile = "[x](file:///etc/passwd) *loud* _name_";

    private static readonly DiagnosticWriter Writer = new(NullLogger<DiagnosticWriter>.Instance, []);

    private static string Format<T>(IValueFormatter<T> formatter, T value, DiagnosticWriterMode mode) where T : notnull
    {
        var state = new DiagnosticWriterState(mode);
        formatter.Format(Writer, ref state, value);
        return state.StringBuilder.ToString();
    }

    [Theory]
    [InlineData("https://example.com/mods/1")]
    [InlineData("http://example.com/mods/1")]
    public void Test_NamedLink_WritesAMarkdownLinkForAWebPageWithTheNameEscaped(string uri)
    {
        var text = Format(new NamedLinkFormatter(), new NamedLink(Hostile, new Uri(uri)), DiagnosticWriterMode.Markdown);

        text.Should().Be($"[{MarkdownText.Inline(Hostile)}]({uri})");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("smb://host/share")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/2")]
    [InlineData("ftp://example.com/a")]
    public void Test_NamedLink_WritesOnlyTheNameForALinkThatIsNotAWebPage(string uri)
    {
        var text = Format(new NamedLinkFormatter(), new NamedLink(Hostile, new Uri(uri)), DiagnosticWriterMode.Markdown);

        text.Should().Be(MarkdownText.Inline(Hostile)).And.NotContain("](");
    }

    [Fact]
    public void Test_NamedLink_EncodesParenthesesAndSpacesInTheTarget()
    {
        var text = Format(new NamedLinkFormatter(), new NamedLink("Mod", new Uri("https://example.com/a(b)/c d")), DiagnosticWriterMode.Markdown);

        text.Should().Be("[Mod](https://example.com/a%28b%29/c%20d)");
    }

    [Fact]
    public void Test_NamedLink_PlainTextIsTheAddress()
    {
        Format(new NamedLinkFormatter(), new NamedLink(Hostile, new Uri("https://example.com/m")), DiagnosticWriterMode.PlainText)
            .Should().Be("https://example.com/m");
    }

    [Fact]
    public async Task Test_LoadoutReference_EscapesTheNameInMarkdownAndKeepsItInPlainText()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, Hostile);
        var formatter = new LoadoutReferenceFormatter(Connection);

        Format(formatter, loadout.ToReference(), DiagnosticWriterMode.Markdown).Should().Be(MarkdownText.Inline(Hostile)).And.NotContain("](");
        Format(formatter, loadout.ToReference(), DiagnosticWriterMode.PlainText).Should().Be(Hostile);
    }

    [Fact]
    public async Task Test_LoadoutItemGroupReference_EscapesTheNameInMarkdownAndKeepsItInPlainText()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Loadout");
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = Hostile },
        };
        var result = await tx.Commit();
        var saved = result.Remap(group);
        var formatter = new LoadoutItemGroupFormatter(Connection);

        Format(formatter, saved.ToReference(loadout), DiagnosticWriterMode.Markdown).Should().Be(MarkdownText.Inline(Hostile)).And.NotContain("](");
        Format(formatter, saved.ToReference(loadout), DiagnosticWriterMode.PlainText).Should().Be(Hostile);
    }
}
