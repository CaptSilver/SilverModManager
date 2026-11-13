using System.Text;
using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Games.CreationEngine.Tests.OpenMw;
using SilverModManager.Sdk.Loadouts;
using Xunit;
using static SilverModManager.Games.CreationEngine.OpenMw.OpenMwConstants;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind;

/// <summary>
/// OpenMW is its own game now. A Morrowind row over an install that has an openmw.cfg writes Morrowind.ini only and
/// never touches the cfg, including for a loadout that managed the cfg before the OpenMW row existed.
/// </summary>
public class MorrowindRetiredOpenMwTests(ITestOutputHelper helper) : AMorrowindTest<MorrowindRetiredOpenMwTests>(helper)
{
    protected override string? OpenMwConfigText => Install.Config("content=Mod.esp\n");

    /// <summary>
    /// The tree the Apply preview and the status check are drawn from: the retired file is either not in it or has
    /// nothing to do. (A Morrowind loadout reports pending changes after an apply in this harness whatever the
    /// config does, so the status check itself says nothing here.)
    /// </summary>
    private void TheConfigHasNothingPlanned(Loadout.ReadOnly loadout)
    {
        var metadata = Refresh(loadout).Installation;
        var tree = Synchronizer.BuildSyncTree(Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetPreviouslyAppliedDiskState(metadata), Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        if (tree.TryGetValue(OpenMwConfigFile, out var node))
            node.Actions.Should().Be(Actions.DoNothing);
    }

    [Fact]
    public async Task MorrowindNeverWritesOpenMwCfg()
    {
        var original = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Other.esp");

        await Apply(loadout);

        (await Config.ReadAllBytesAsync()).Should().Equal(original);
        (await Ini.ReadAllTextAsync()).Should().Contain("Other.esp", "Morrowind.ini is the Morrowind row's own list");
    }

    [Fact]
    public async Task APreChangeOpenMwModeLoadoutAppliesWithTheConfigUntouched()
    {
        var original = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp");
        await Apply(loadout);

        // What an apply of the old OpenMW mode left recorded: openmw.cfg as part of the last applied disk state.
        using (var tx = Connection.BeginTransaction())
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = OpenMwConfigFile.ToGamePathParentTuple(loadout.Installation),
                Identity = FileIdentity.XxHash3(original.xxHash3()),
                XxHash3 = original.xxHash3(),
                Size = Size.FromLong(original.Length),
                LastModified = DateTime.UtcNow,
                GameId = loadout.Installation,
            };
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadout, loadout.Id);
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
            await tx.Commit();
        }

        await Apply(loadout);

        Config.FileExists.Should().BeTrue();
        (await Config.ReadAllBytesAsync()).Should().Equal(original, "the retired mode's file is never deleted or rewritten");

        (await FileStore.HaveFile(FileIdentity.XxHash3(original.xxHash3()))).Should().BeFalse("deleting the file would have backed it up first");
        TheConfigHasNothingPlanned(loadout);

        var warning = (await Game.Diagnose(Refresh(loadout))).Should().ContainSingle(d => d.Id.Number == 17).Subject;
        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
        var summary = warning.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain("OpenMW is now its own game").And.Contain("Mod.esp");
    }

    [Fact]
    public async Task ALoadoutThatNeverManagedTheConfigGetsNoRetirementWarning()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp");
        await Apply(loadout);

        (await Game.Diagnose(Refresh(loadout))).Should().NotContain(d => d.Id.Number == 17);
    }

    [Theory]
    [InlineData("settings.cfg")]
    [InlineData("launcher.cfg")]
    [InlineData("openmw.cfg")]
    public void EveryOpenMwConfigPathIsProtected(string path) =>
        Game.IsProtectedPath(new GamePath(OpenMwConfig, path)).Should().BeTrue();

    [Fact]
    public void TheIniMechanismIsUsedWhetherOrNotAConfigExists()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<CreationEngine.PluginLists.MorrowindIniMechanism>();
        Game.MasterFlagIgnored(GameInstallation).Should().BeFalse();
        Game.EarlyLoadingPlugins(GameInstallation).Should().BeEmpty();
    }
}

/// <summary>
/// The game has both a Morrowind.ini and an openmw.cfg that disagree about the order: the ini decides for the
/// Morrowind row, the first Apply writes the ini in that order with its other sections as they were, and the cfg is
/// left alone.
/// </summary>
public class MorrowindIniWithAnOpenMwConfigTests(ITestOutputHelper helper) : AMorrowindTest<MorrowindIniWithAnOpenMwConfigTests>(helper)
{
    protected override string[] PluginsOnDisk => ["A.esp", "B.esp"];

    // The cfg loads B before A; the ini says the opposite.
    protected override string? OpenMwConfigText => Install.Config("content=B.esp\ncontent=A.esp\n");

    protected override string? MorrowindIni =>
        "[General]\r\nScreen Width=1920\r\n[Game Files]\r\nGameFile0=Morrowind.esm\r\nGameFile1=A.esp\r\nGameFile2=B.esp\r\n[Weather]\r\nSunrise Time=6\r\n";

    [Fact]
    public async Task MorrowindIniIsWrittenByTheMorrowindRowOnly()
    {
        var config = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();

        await Apply(loadout);

        var ini = await Ini.ReadAllTextAsync();
        ini.Should().StartWith("[General]\r\nScreen Width=1920\r\n").And.EndWith("[Weather]\r\nSunrise Time=6\r\n");
        ini.Split("\r\n").Where(line => line.StartsWith("GameFile")).Select(line => line[(line.IndexOf('=') + 1)..])
            .Where(name => name != "Morrowind.esm")
            .Should().Equal("A.esp", "B.esp");
        (await Config.ReadAllBytesAsync()).Should().Equal(config);
    }
}
