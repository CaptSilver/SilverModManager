using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class SkyrimSERoundTripTests(ITestOutputHelper helper)
    : ACreationEngineRoundTripTest<SkyrimSERoundTripTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override Version GameVersion => new("1.6.1170");
    protected override string PrimaryExe => "SkyrimSE.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    protected override string Marker => "*";
    protected override GameRelease Release => GameRelease.SkyrimSE;
}
