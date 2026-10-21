using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class SkyrimLERoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<SkyrimLERoundTripTests, CreationEngine.SkyrimLE.SkyrimLE>(helper)
{
    protected override Version GameVersion => new("1.9.32");
    protected override string PrimaryExe => "TESV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
