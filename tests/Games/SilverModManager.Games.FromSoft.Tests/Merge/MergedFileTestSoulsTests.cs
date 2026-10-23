using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>The same apply-and-merge path on a BND3 game with no regulation, so nothing Elden Ring is assumed.</summary>
public class MergedFileTestSoulsTests(ITestOutputHelper helper) : AModdedTestSoulsTest<MergedFileTestSoulsTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private FromSoftFormatTraits Formats => ((IFromSoftGame)GameInstallation.Game).Formats;

    private AbsolutePath Mods => GameInstallation.Locations[LocationId.Me3Mods].Path;

    private byte[] Msgbnd(string text, int ownId)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text, [ownId] = "own" },
        }, Formats);

    private async Task InstallMod(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
    }

    [Fact]
    public async Task OverlappingMessageArchivesMergeIntoOneFileInTheMergedPackage()
    {
        var loadout = await CreateLoadout();
        var alpha = Msgbnd("alpha text", 11);
        var beta = Msgbnd("beta text", 12);
        await InstallMod(loadout.LoadoutId, "Alpha", (Msg, alpha), ("parts/alpha.dcx", [1]));
        await InstallMod(loadout.LoadoutId, "Beta", (Msg, beta));

        await Synchronizer.Synchronize(Refresh(loadout));

        var merged = await (Mods / ("_merged/" + Msg)).ReadAllBytesAsync();
        merged.Should().Equal(FmgMerge.Union(beta, alpha, Formats));
        MsgbndFixture.ReadBack(merged, Formats)[1][10].Should().Be("beta text");
        Directory.GetFiles(Mods.GetFullPath(), "item.msgbnd.dcx", SearchOption.AllDirectories).Should().ContainSingle();
        File.ReadAllText((Mods / "silver.me3").GetFullPath()).Should().Contain("id = \"_merged\"").And.NotContain("id = \"Beta\"");
    }
}
