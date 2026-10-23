using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.SortOrder;

namespace SilverModManager.Games.CreationEngine.Tests.Modlists;

public class PluginOrderMergeTests
{
    private static readonly ISet<string> Early = new HashSet<string>(["Skyrim.esm", "Update.esm"], PluginNameComparer.Instance);

    [Fact]
    public void LockedRowsComeFirst_ThenTheListedNames_ThenTheUnlistedRowsInTheirOrder()
    {
        var existing = new[]
        {
            new PluginSortData("Unlisted2.esp", 4, true),
            new PluginSortData("Skyrim.esm", 0, true, earlyLoaderIndex: 0),
            new PluginSortData("Unlisted1.esp", 3, true),
            new PluginSortData("Update.esm", 1, true, earlyLoaderIndex: 1),
            new PluginSortData("Listed.esp", 2, true),
        };

        var merged = PluginOrderMerge.Build(existing, [("Update.esm", true), ("listed.ESP", false), ("New.esp", true)], Early);

        merged.Should().Equal(
            ("Skyrim.esm", (bool?)null), ("Update.esm", null),
            ("Listed.esp", false), ("New.esp", true),
            ("Unlisted1.esp", null), ("Unlisted2.esp", null));
    }

    [Fact]
    public void AListedNameThatRepeats_IsKeptOnce()
    {
        var merged = PluginOrderMerge.Build([], [("A.esp", true), ("a.esp", false), ("B.esp", true)], Early);

        merged.Should().Equal(("A.esp", (bool?)true), ("B.esp", true));
    }

    [Fact]
    public void AnEarlyLoaderTheLoadoutHasNoRowFor_IsLeftOut()
    {
        var merged = PluginOrderMerge.Build([], [("Skyrim.esm", true), ("A.esp", true)], Early);

        merged.Should().Equal(("A.esp", (bool?)true));
    }
}
