using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class ReadyOrNotTests(ITestOutputHelper outputHelper) : AUnrealRowTests<ReadyOrNotTests>(outputHelper, UnrealGames.ReadyOrNot);
