using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class FF7RebirthTests(ITestOutputHelper outputHelper) : AUnrealRowTests<FF7RebirthTests>(outputHelper, UnrealGames.FinalFantasy7Rebirth);
