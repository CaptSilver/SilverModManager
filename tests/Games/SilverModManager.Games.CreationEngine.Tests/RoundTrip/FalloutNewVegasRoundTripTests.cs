using FluentAssertions;
using Mutagen.Bethesda;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class FalloutNewVegasRoundTripTests(ITestOutputHelper helper)
    : ATimestampRoundTripTest<FalloutNewVegasRoundTripTests, CreationEngine.FalloutNewVegas.FalloutNewVegas>(helper)
{
    protected override Version GameVersion => new("1.4.0.525");
    protected override string PrimaryExe => "FalloutNV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");

    // Same 24-byte record headers as Skyrim; Mutagen has no New Vegas release of its own.
    protected override GameRelease Release => GameRelease.SkyrimLE;

    [Fact]
    public async Task APluginWithANamFileIsListedActiveEvenWhenSwitchedOff()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallMod(loadout, new Dictionary<RelativePath, byte[]>
        {
            { "Data/Voiced.esp", PluginBytes("Voiced.esp") },
            { "Data/Voiced.nam", "nam"u8.ToArray() },
        });
        await InstallPlugin(loadout, "Other.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        var sortOrder = variety.SortOrderOf(Refresh(loadout), Connection);
        await variety.SetEnabled(sortOrder, PluginSortOrderTestHelpers.Key("Voiced.esp"), false);
        await variety.SetEnabled(sortOrder, PluginSortOrderTestHelpers.Key("Other.esp"), false);
        await Apply(loadout);

        var lines = await PluginLines();
        lines.Skip(1).Should().Equal("Voiced.esp");
    }
}
