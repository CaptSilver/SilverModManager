using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class ValheimTests(ITestOutputHelper outputHelper) : ABepInExRowTests<ValheimTests>(outputHelper, UnityGames.Valheim);
