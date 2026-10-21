using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class EnderalRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<EnderalRoundTripTests, CreationEngine.Enderal.Enderal>(helper)
{
    protected override Version GameVersion => new("2.0.12");
    protected override string PrimaryExe => "TESV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
