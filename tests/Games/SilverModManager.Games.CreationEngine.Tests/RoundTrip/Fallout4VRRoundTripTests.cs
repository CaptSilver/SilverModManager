using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class Fallout4VRRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<Fallout4VRRoundTripTests, CreationEngine.Fallout4VR.Fallout4VR>(helper)
{
    protected override Version GameVersion => new("1.2.72");
    protected override string PrimaryExe => "Fallout4VR.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
