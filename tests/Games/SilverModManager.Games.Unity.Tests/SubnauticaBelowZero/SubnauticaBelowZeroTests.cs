using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class SubnauticaBelowZeroTests(ITestOutputHelper outputHelper) : ABepInExRowTests<SubnauticaBelowZeroTests>(outputHelper, UnityGames.SubnauticaBelowZero);
