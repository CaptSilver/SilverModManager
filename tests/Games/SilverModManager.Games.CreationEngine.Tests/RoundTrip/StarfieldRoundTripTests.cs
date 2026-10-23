using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class StarfieldRoundTripTests(ITestOutputHelper helper)
    : ACreationEngineRoundTripTest<StarfieldRoundTripTests, CreationEngine.Starfield.Starfield>(helper)
{
    protected override Version GameVersion => new("1.15.222");
    protected override string PrimaryExe => "Starfield.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    protected override string Marker => "*";
    protected override GameRelease Release => GameRelease.Starfield;
}
