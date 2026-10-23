using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class SkyrimLERoundTripTests(ITestOutputHelper helper)
    : ATextfileRoundTripTest<SkyrimLERoundTripTests, CreationEngine.SkyrimLE.SkyrimLE>(helper)
{
    protected override Version GameVersion => new("1.9.32");
    protected override string PrimaryExe => "TESV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    protected override GameRelease Release => GameRelease.SkyrimLE;
    protected override string[] LeadingPlugins => ["Skyrim.esm", "Update.esm"];
}
