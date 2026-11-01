using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

public class Stalker2Tests(ITestOutputHelper outputHelper) : AUnrealRowTests<Stalker2Tests>(outputHelper, UnrealGames.Stalker2);
