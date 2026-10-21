using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class MorrowindRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<MorrowindRoundTripTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    protected override Version GameVersion => new("1.6.0.1820");
    protected override string PrimaryExe => "Morrowind.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data Files");
}
