using FluentAssertions;
using SilverModManager.Games.Witcher.Witcher3;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class ModsSettingsDocumentTests
{
    [Fact]
    public Task Render_WritesTheLockedRowFirstWithPriorityOne() =>
        Verify(ModsSettingsDocument.Render([("mod0000_MergedFiles", true), ("modX", true)]));

    [Fact]
    public void Render_WritesEnabledZeroForASwitchedOffFolder()
    {
        var text = ModsSettingsDocument.Render([("mod0000_MergedFiles", true), ("modA", false), ("modB", true)]);

        text.Should().Be("[mod0000_MergedFiles]\r\nEnabled=1\r\nPriority=1\r\n\r\n[modA]\r\nEnabled=0\r\nPriority=2\r\n\r\n[modB]\r\nEnabled=1\r\nPriority=3\r\n");
    }

    [Fact]
    public void Render_WritesOneSectionForTwoSpellingsOfAFolder()
    {
        var text = ModsSettingsDocument.Render([("mod0000_MergedFiles", true), ("modSDL", true), ("ModSdl", false), ("modB", true)]);

        ModsSettingsDocument.Parse(text).Select(s => (s.Folder, s.Priority))
            .Should().Equal(("mod0000_MergedFiles", 1), ("modSDL", 2), ("modB", 3));
    }

    [Fact]
    public void Parse_IgnoresVortexKeysAndDuplicatePriorities()
    {
        var sections = ModsSettingsDocument.Parse("[modA]\nEnabled=1\nPriority=2\nVK=modA\n[modB]\nPriority=2\n");

        sections.Should().Equal(new ModsSettingsDocument.Section("modA", true, 2), new ModsSettingsDocument.Section("modB", true, 2));
    }

    [Fact]
    public void Parse_TakesAMissingPriorityAsLast()
    {
        ModsSettingsDocument.Parse("[modA]\nEnabled=0\n").Should().Equal(new ModsSettingsDocument.Section("modA", false, int.MaxValue));
    }

    [Fact]
    public void Parse_ReadsCaseInsensitiveKeysAndSkipsComments()
    {
        ModsSettingsDocument.Parse("; note\r\n[modA]\r\nenabled=0\r\npriority=5\r\n").Should()
            .Equal(new ModsSettingsDocument.Section("modA", false, 5));
    }

    [Fact]
    public void Parse_RoundTripsRender()
    {
        (string, bool)[] order = [("mod0000_MergedFiles", true), ("modB", false), ("modA", true)];

        ModsSettingsDocument.Parse(ModsSettingsDocument.Render(order))
            .Select(s => (s.Folder, s.Enabled)).Should().Equal(order);
    }

    [Theory]
    [InlineData("\0\0", false)]
    [InlineData("just some words", false)]
    [InlineData("", true)]
    [InlineData("[modA]\nPriority=1\n", true)]
    public void CanParse_RejectsWhatIsNotAnIniFile(string text, bool expected) =>
        ModsSettingsDocument.CanParse(text).Should().Be(expected);
}
