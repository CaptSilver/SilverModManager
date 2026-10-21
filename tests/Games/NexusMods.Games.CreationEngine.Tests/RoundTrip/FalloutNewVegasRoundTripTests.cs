using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class FalloutNewVegasRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<FalloutNewVegasRoundTripTests, CreationEngine.FalloutNewVegas.FalloutNewVegas>(helper)
{
    protected override Version GameVersion => new("1.4.0.525");
    protected override string PrimaryExe => "FalloutNV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
