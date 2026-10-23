using Mutagen.Bethesda;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class EnderalRoundTripTests(ITestOutputHelper helper)
    : ATextfileRoundTripTest<EnderalRoundTripTests, CreationEngine.Enderal.Enderal>(helper)
{
    protected override Version GameVersion => new("2.0.12");
    protected override string PrimaryExe => "TESV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    protected override GameRelease Release => GameRelease.EnderalLE;
    protected override string[] LeadingPlugins => ["Skyrim.esm", "Update.esm"];
}
