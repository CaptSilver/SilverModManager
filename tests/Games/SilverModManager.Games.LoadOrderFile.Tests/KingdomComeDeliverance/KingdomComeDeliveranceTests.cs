using FluentAssertions;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class KingdomComeDeliveranceTests(ITestOutputHelper outputHelper)
    : AKingdomComeRowTests<KingdomComeDeliveranceTests>(outputHelper, LoadOrderFileGames.KingdomComeDeliverance);

public class KingdomComeDeliverance2Tests(ITestOutputHelper outputHelper)
    : AKingdomComeRowTests<KingdomComeDeliverance2Tests>(outputHelper, LoadOrderFileGames.KingdomComeDeliverance2);

/// <summary>Both Kingdom Come games take a mod as a folder under Mods, named for the mod, and list those folders in mod_order.txt.</summary>
public abstract class AKingdomComeRowTests<TTest>(ITestOutputHelper outputHelper, LoadOrderFileGameDefinition definition)
    : ALoadOrderFileRowTests<TTest>(outputHelper, definition)
{
    protected override string[] ArchivePathsOf(string mod) => [$"{mod}/mod.manifest", $"{mod}/Data/{mod}.pak"];

    [Fact]
    public void ModsLiveInTheModsFolderAndTheOrderFileBesideThem()
    {
        Definition.ModFolder.ToString().Should().Be("Mods");
        Definition.OrderFile.ToString().Should().Be("Mods/mod_order.txt");
    }

    [Fact]
    public async Task AModFolderInTheArchiveInstallsBelowMods()
    {
        var targets = await InstallTargets(["Cool/mod.manifest", "Cool/Data/cool.pak"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AModsFolderInTheArchiveIsNotNestedAgain()
    {
        var targets = await InstallTargets(["Mods/Cool/mod.manifest", "Mods/Cool/Data/cool.pak"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AModAtTheTopOfTheArchiveGetsAFolderNamedAfterTheArchive()
    {
        var targets = await InstallTargets(["mod.manifest", "Data/cool.pak"], "Cool Mod.zip");

        targets.Should().Equal(OnGame("Mods/Cool Mod/Data/cool.pak"), OnGame("Mods/Cool Mod/mod.manifest"));
    }

    [Fact]
    public async Task AReadmeBesideTheModFolderIsLeftOut()
    {
        var targets = await InstallTargets(["Cool/mod.manifest", "Cool/Data/cool.pak", "readme.txt"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AnArchiveWithoutAModManifestIsNotSupported()
    {
        var targets = await InstallTargets(["Cool/Data/cool.pak"]);

        targets.Should().BeNull();
    }
}
