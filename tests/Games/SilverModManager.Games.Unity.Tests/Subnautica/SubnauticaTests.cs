using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class SubnauticaTests(ITestOutputHelper outputHelper) : ABepInExRowTests<SubnauticaTests>(outputHelper, UnityGames.Subnautica);
