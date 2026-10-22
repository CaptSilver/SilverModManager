using FluentAssertions;
using Mutagen.Bethesda;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using LoadOrderTests = NexusMods.Games.Bethesda.LoadOrder.Tests;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class OblivionRemasteredRoundTripTests(ITestOutputHelper helper)
    : ATextfileRoundTripTest<OblivionRemasteredRoundTripTests, CreationEngine.OblivionRemastered.OblivionRemastered>(helper)
{
    protected override Version GameVersion => new("1.2.0.0");
    protected override string PrimaryExe => "OblivionRemastered.exe";
    protected override GamePath DataPath => new(LocationId.Game, "OblivionRemastered/Content/Dev/ObvData/Data");
    protected override GamePath PluginsFile => DataPath / "Plugins.txt";
    protected override GameRelease Release => GameRelease.Oblivion;

    [Fact]
    public async Task AMasterFlaggedPluginIsNotHoistedAboveTheOnesBeforeIt()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "Alpha.esp");
        // The flag every other title reads as master, which would put Flagged.esm ahead of Alpha.esp.
        var flagged = LoadOrderTests.PluginBytes.Write(PluginFormat.Tes4Oblivion, new LoadOrderTests.PluginSpec { RecordFlags = 0x1 });
        await InstallMod(loadout, new Dictionary<RelativePath, byte[]> { { "Data/Flagged.esm", flagged } });
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("Alpha.esp", "Flagged.esm");
        (await PluginLines()).Skip(1).Should().Equal("Alpha.esp", "Flagged.esm");
    }
}
