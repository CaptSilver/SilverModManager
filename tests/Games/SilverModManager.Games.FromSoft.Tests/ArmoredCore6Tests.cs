using SilverModManager.Games.FromSoft.ArmoredCore6;
using SilverModManager.Games.FromSoft.Tests.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests;

public class ArmoredCore6Tests(ITestOutputHelper helper)
    : AFromSoftInstalledGameTests<ArmoredCore6Tests, ArmoredCore6Game>(helper, FromSoftGameFacts.ArmoredCore6);
