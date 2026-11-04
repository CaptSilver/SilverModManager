using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class HogwartsLegacyTests(ITestOutputHelper outputHelper) : AUnrealRowTests<HogwartsLegacyTests>(outputHelper, UnrealGames.HogwartsLegacy);
