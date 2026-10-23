using FluentAssertions;
using Mutagen.Bethesda;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class OblivionRoundTripTests(ITestOutputHelper helper)
    : ATimestampRoundTripTest<OblivionRoundTripTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    protected override Version GameVersion => new("1.2.0.416");
    protected override string PrimaryExe => "Oblivion.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    protected override GameRelease Release => GameRelease.Oblivion;

    [Fact]
    public async Task WithBUseMyGamesDirectoryOffThePluginsFileSitsInTheGameFolder()
    {
        var ini = OnDisk(new GamePath(LocationId.Game, "Oblivion.ini"));
        await ini.WriteAllTextAsync("[General]\nbUseMyGamesDirectory=0\n");
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "First.esp");

        await Apply(loadout);

        var inGameFolder = OnDisk(new GamePath(LocationId.Game, "Plugins.txt"));
        inGameFolder.FileExists.Should().BeTrue();
        (await inGameFolder.ReadAllTextAsync()).Should().Contain("First.esp");
        OnDisk(PluginsFile).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task WithBUseMyGamesDirectoryOnThePluginsFileSitsInAppData()
    {
        var ini = OnDisk(new GamePath(LocationId.Game, "Oblivion.ini"));
        await ini.WriteAllTextAsync("[General]\nbUseMyGamesDirectory=1\n");
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "First.esp");

        await Apply(loadout);

        (await PluginLines()).Should().Contain("First.esp");
        OnDisk(new GamePath(LocationId.Game, "Plugins.txt")).FileExists.Should().BeFalse();
    }
}
