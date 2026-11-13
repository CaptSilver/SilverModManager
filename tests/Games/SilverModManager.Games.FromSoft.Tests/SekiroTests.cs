using FluentAssertions;
using SilverModManager.Games.FromSoft.Sekiro;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class SekiroTests(ITestOutputHelper helper)
    : AFromSoftInstalledGameTests<SekiroTests, SekiroGame>(helper, FromSoftGameFacts.Sekiro)
{
    [Fact]
    public void EveryGamePathAtTheRootIsNeverWrittenAndNoMe3PathIs()
    {
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "Artwork_MiniSoundtrack/x.exe")).Should().BeTrue();
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Me3Mods, "parts/x.dcx")).Should().BeFalse();
    }
}
