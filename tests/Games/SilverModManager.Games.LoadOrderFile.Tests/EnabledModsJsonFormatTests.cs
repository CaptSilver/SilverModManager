using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class EnabledModsJsonFormatTests
{
    private static readonly EnabledModsJsonFormat Format = new();

    [Fact]
    public void RenderIsPrettyPrintedAndSorted()
    {
        Format.Render(["b", "a"], null).Should().Be("{\n  \"enabledMods\": [\n    \"a\",\n    \"b\"\n  ]\n}");
    }

    [Fact]
    public void RenderSortsOrdinallyNotByCulture()
    {
        Format.Render(["a", "B", "_c"], null).Should().Be("{\n  \"enabledMods\": [\n    \"B\",\n    \"_c\",\n    \"a\"\n  ]\n}");
    }

    [Fact]
    public void RenderOfNoModsIsAnEmptyList()
    {
        Format.Render([], null).Should().Be("{\n  \"enabledMods\": []\n}");
    }

    [Fact]
    public void RenderIgnoresTheCurrentFile()
    {
        Format.Render(["a"], "{ \"enabledMods\": [\"zzz\"] }").Should().Be(Format.Render(["a"], null));
    }

    [Fact]
    public void ListedReadsEnabledModsInOrder()
    {
        Format.Listed("{\"enabledMods\":[\"Beta\",\"alpha\"]}").Should().Equal("Beta", "alpha");
    }

    [Fact]
    public void ListedOnAnEmptyObjectIsEmpty()
    {
        Format.Listed("{}").Should().BeEmpty();
    }

    [Fact]
    public void ListedOnBrokenJsonIsEmpty()
    {
        Format.Listed("{\"enabledMods\": [").Should().BeEmpty();
    }

    [Fact]
    public void ListedOnAnArrayRootIsEmpty()
    {
        Format.Listed("[\"a\"]").Should().BeEmpty();
    }

    [Fact]
    public void ListedSkipsEntriesThatAreNotStrings()
    {
        Format.Listed("{\"enabledMods\":[\"a\",3,null,\"b\"]}").Should().Equal("a", "b");
    }

    [Fact]
    public void ListedReadsTheRenderedFileBack()
    {
        Format.Listed(Format.Render(["b", "a"], null)).Should().Equal("a", "b");
    }

    [Theory]
    [InlineData("Alpha/mod_info.json", "Alpha")]
    [InlineData("Alpha/data/x.csv", "Alpha")]
    public void ModOfIsTheFirstFolder(string file, string mod)
    {
        Format.ModOf((RelativePath)file).Should().Be(mod);
    }

    [Fact]
    public void AFileDirectlyInTheModFolderIsNobodys()
    {
        Format.ModOf((RelativePath)"enabled_mods.json").Should().BeNull();
    }
}
