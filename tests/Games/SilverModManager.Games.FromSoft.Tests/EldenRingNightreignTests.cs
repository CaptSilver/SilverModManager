using SilverModManager.Games.FromSoft.EldenRingNightreign;
using SilverModManager.Games.FromSoft.Tests.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests;

public class EldenRingNightreignTests(ITestOutputHelper helper)
    : AFromSoftInstalledGameTests<EldenRingNightreignTests, EldenRingNightreignGame>(helper, FromSoftGameFacts.EldenRingNightreign);
