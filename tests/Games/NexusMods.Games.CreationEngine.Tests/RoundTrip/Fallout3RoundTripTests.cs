using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class Fallout3RoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<Fallout3RoundTripTests, CreationEngine.Fallout3.Fallout3>(helper)
{
    protected override Version GameVersion => new("1.7.0.3");
    protected override string PrimaryExe => "Fallout3.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
