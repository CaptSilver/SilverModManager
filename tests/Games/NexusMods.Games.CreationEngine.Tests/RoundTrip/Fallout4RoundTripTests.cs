using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class Fallout4RoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<Fallout4RoundTripTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override Version GameVersion => new("1.10.163");
    protected override string PrimaryExe => "Fallout4.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
