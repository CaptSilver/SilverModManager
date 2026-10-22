using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Tests.Heal;

/// <summary>
/// A game that is patched after a first apply, with two mods that ship regulations built for the build before the
/// patch: the merge still has to carry both mods' rows onto the new build and leave the game folder alone.
/// </summary>
public class HealEndToEndTests(ITestOutputHelper helper) : AVanillaDumpTest<HealEndToEndTests>(helper)
{
    private const string Table = "TEST_PARAM_ST";
    private const string TableMember = Table + ".param";
    private const int Stride = 8;

    private AbsolutePath Mods => GameInstallation.Locations[LocationId.Me3Mods].Path;

    private static byte[] Row(byte fill) => Enumerable.Repeat(fill, Stride).ToArray();

    private static byte[] Regulation(string version, params (int Id, byte Fill)[] rows)
        => RegulationFixture.Build(
            new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
            {
                [1] = (Table, rows.ToDictionary(r => r.Id, r => Row(r.Fill)), Stride),
            }, version, Formats);

    private static IReadOnlyDictionary<int, byte[]> RowsOf(byte[] regulation)
    {
        var entries = Binders.For(Formats).Read(FromSoft.Formats.Regulation.Unpack(regulation, Formats));
        return RebaseVerifier.RowsByTable(entries)[TableMember];
    }

    private void WriteGame(byte[] regulation) => File.WriteAllBytes((RealGameFolder / "regulation.bin").GetFullPath(), regulation);

    [Fact]
    public async Task AGamePatchedAfterTheFirstApplyIsMergedOntoTheNewBuildWithBothModsRows()
    {
        var vanilla = Regulation(OlderVersion, (1, 0x01), (2, 0x02));
        var alpha = Regulation(OlderVersion, (1, 0xA1), (2, 0x02));
        var beta = Regulation(OlderVersion, (1, 0x01), (2, 0xB2));
        WriteGame(vanilla);
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", ("regulation.bin", alpha));
        await InstallPackage(loadout.LoadoutId, "Beta", ("regulation.bin", beta));
        loadout = Refresh(loadout);
        await Synchronizer.Synchronize(loadout);
        File.ReadAllBytes((DataRoot / "baselines" / $"regulation-{OlderVersion}.bin").GetFullPath()).Should().Equal(vanilla);
        var stamped = BuildIdentity.Identify(GameInstallation);
        await ServiceProvider.GetRequiredService<BuildStampStore>().Record(loadout.InstallationId, stamped);

        // The patch moves the regulation, the executable and Steam's build id, and adds a row of its own.
        var patched = Regulation(NewerVersion, (1, 0x01), (2, 0x02), (3, 0x03));
        WriteGame(patched);
        File.WriteAllBytes((RealGameFolder / "eldenring.exe").GetFullPath(), FakePe.WithVersion(new Version(2, 8, 0, 0)));
        EldenRingInstall.SetSteamBuildId(Install, "99999999");
        Relocate();
        var live = BuildIdentity.Identify(GameInstallation);
        BuildIdentity.Classify(stamped, live).Should().Be(BuildChangeKind.Patched);
        var gameBefore = SnapshotOfGameFolder();

        await Synchronizer.Synchronize(Refresh(loadout));

        var merged = File.ReadAllBytes((Mods / "_merged/regulation.bin").GetFullPath());
        FromSoft.Formats.Regulation.Version(FromSoft.Formats.Regulation.Unpack(merged, Formats)).Should().Be(NewerVersion);
        var rows = RowsOf(merged);
        rows[1].Should().Equal(Row(0xA1));
        rows[2].Should().Equal(Row(0xB2));
        rows[3].Should().Equal(Row(0x03));
        File.Exists((DataRoot / "baselines" / $"regulation-{OlderVersion}.bin").GetFullPath()).Should().BeTrue();
        SnapshotOfGameFolder().Should().Equal(gameBefore);
        RebaseVerifier.Verify(merged, patched, [("Alpha", alpha), ("Beta", beta)], live, Formats).Should().BeEmpty();

        var entry = (await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None))
            .Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be("regulation.bin");
        entry.Strategy.Should().Be("param-rows");
        entry.Contributors.Should().Contain(c => c.Contains("Alpha")).And.Contain(c => c.Contains("Beta"));
    }
}
