using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class PalworldTests(ITestOutputHelper outputHelper) : AUnrealRowTests<PalworldTests>(outputHelper, UnrealGames.Palworld);
