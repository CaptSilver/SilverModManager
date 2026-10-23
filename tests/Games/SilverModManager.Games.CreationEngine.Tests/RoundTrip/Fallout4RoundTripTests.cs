using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class Fallout4RoundTripTests(ITestOutputHelper helper)
    : ACreationEngineRoundTripTest<Fallout4RoundTripTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override Version GameVersion => new("1.10.163");
    protected override string PrimaryExe => "Fallout4.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    protected override string Marker => "*";
    protected override GameRelease Release => GameRelease.Fallout4;
}
