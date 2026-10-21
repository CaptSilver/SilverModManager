using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class OblivionRemasteredRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<OblivionRemasteredRoundTripTests, CreationEngine.OblivionRemastered.OblivionRemastered>(helper)
{
    protected override Version GameVersion => new("1.2.0.0");
    protected override string PrimaryExe => "OblivionRemastered.exe";
    protected override GamePath DataPath => new(LocationId.Game, "OblivionRemastered/Content/Dev/ObvData/Data");
}
