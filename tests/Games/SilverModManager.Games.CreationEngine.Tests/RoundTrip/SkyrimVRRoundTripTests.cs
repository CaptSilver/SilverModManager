using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class SkyrimVRRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<SkyrimVRRoundTripTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    protected override Version GameVersion => new("1.4.15");
    protected override string PrimaryExe => "SkyrimVR.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
