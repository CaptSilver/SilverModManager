using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class Fallout76RoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<Fallout76RoundTripTests, CreationEngine.Fallout76.Fallout76>(helper)
{
    protected override Version GameVersion => new("1.7.0");
    protected override string PrimaryExe => "Fallout76.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
