using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class SkyrimSERoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<SkyrimSERoundTripTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override Version GameVersion => new("1.6.1170");
    protected override string PrimaryExe => "SkyrimSE.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
