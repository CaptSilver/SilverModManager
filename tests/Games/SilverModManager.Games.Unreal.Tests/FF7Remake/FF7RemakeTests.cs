using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class FF7RemakeTests(ITestOutputHelper outputHelper) : AUnrealRowTests<FF7RemakeTests>(outputHelper, UnrealGames.FinalFantasy7Remake);
