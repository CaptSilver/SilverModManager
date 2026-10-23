using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

public class EnderalSERoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<EnderalSERoundTripTests, CreationEngine.EnderalSE.EnderalSE>(helper)
{
    protected override Version GameVersion => new("2.0.12");
    protected override string PrimaryExe => "SkyrimSE.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
