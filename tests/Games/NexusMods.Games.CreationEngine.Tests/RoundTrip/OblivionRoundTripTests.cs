using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class OblivionRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<OblivionRoundTripTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    protected override Version GameVersion => new("1.2.0.416");
    protected override string PrimaryExe => "Oblivion.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
}
