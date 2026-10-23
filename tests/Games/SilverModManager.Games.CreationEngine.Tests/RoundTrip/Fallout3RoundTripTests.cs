using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class Fallout3RoundTripTests(ITestOutputHelper helper)
    : ATimestampRoundTripTest<Fallout3RoundTripTests, CreationEngine.Fallout3.Fallout3>(helper)
{
    protected override Version GameVersion => new("1.7.0.3");
    protected override string PrimaryExe => "Fallout3.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");

    // Same 24-byte record headers as Skyrim; Mutagen has no Fallout 3 release of its own.
    protected override GameRelease Release => GameRelease.SkyrimLE;
}
