using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class ValheimTests(ITestOutputHelper outputHelper) : AUnityRowTests<ValheimTests>(outputHelper, UnityGames.Valheim);
