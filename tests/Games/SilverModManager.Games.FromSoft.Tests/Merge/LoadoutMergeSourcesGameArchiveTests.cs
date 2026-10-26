using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// A packed file no library archive holds a vanilla copy of is read from the game's own archives. Test Souls has no
/// regulation, so no library dump can ever offer a file and the game's archives are the only possible source.
/// </summary>
public class LoadoutMergeSourcesGameArchiveTests(ITestOutputHelper helper) : AModdedTestSoulsTest<LoadoutMergeSourcesGameArchiveTests>(helper)
{
    private const string Talk = "script/talk/m00.talkesdbnd.dcx";
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private static readonly byte[] Vanilla = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    private FromSoftFormatTraits Formats => ((IFromSoftGame)GameInstallation.Game).Formats;

    private void WriteGameArchive(params (string Path, byte[] Stored)[] files)
        => GameArchiveFixture.Write(RealGameFolder.GetFullPath(), "TestData0", Bhd5Fixture.TestKey, Formats, encrypted: true, lead: 48, files);

    private async Task<Loadout.ReadOnly> TwoMods(params string[] paths)
    {
        var loadout = await CreateLoadout();
        foreach (var name in new[] { "Alpha", "Beta" })
        {
            var archive = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
            await TestArchives.WriteEntries(archive, paths.Select(p => (p, new[] { (byte)name[0] })).ToArray());
            var registered = await RegisterLocalArchive(archive);
            var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
            await LoadoutManager.InstallItem(registered.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
        }
        return Refresh(loadout);
    }

    [Fact]
    public async Task APackedFileTheGameArchivesHoldMergesInsteadOfNeedingADump()
    {
        WriteGameArchive((Talk, Vanilla));
        var loadout = await TwoMods(Talk);

        var candidate = Sources.Index(loadout).Lookup(Talk)!;

        candidate.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        candidate.Outcome.Merges.Should().BeTrue();
    }

    [Fact]
    public async Task TheGameArchivesCopyIsTheAncestorAndTheNoteSaysWhereItCameFrom()
    {
        WriteGameArchive((Talk, Vanilla));
        var loadout = await TwoMods(Talk);
        var contributors = Sources.Index(loadout).Lookup(Talk)!.Contributors;
        var notes = new List<IMergeNote>();

        Sources.SingleVanilla(loadout, Talk, contributors, MergeSourceMode.Report, notes).Should().Equal(Vanilla);

        notes.Should().ContainSingle().Which.Should().Be(new VanillaServedFromGameArchive(Talk));
        notes[0].Should().BeAssignableTo<IVanillaSourceNote>();
        notes[0].Describe().Should().Contain(Talk).And.Contain("game's own archives");
    }

    [Fact]
    public async Task APathTheGameArchivesDoNotHoldStillHasNoVanillaSource()
    {
        WriteGameArchive((Msg, Vanilla));
        var loadout = await TwoMods(Talk);

        var candidate = Sources.Index(loadout).Lookup(Talk)!;

        candidate.Outcome.Merges.Should().BeFalse();
        candidate.Outcome.Reason.Should().Be(MergeOutcome.NoVanillaSource);
        Sources.SingleVanilla(loadout, Talk, candidate.Contributors, MergeSourceMode.Report).Should().BeNull();
    }

    [Fact]
    public async Task AMessageArchiveInTheGameArchivesMovesFromAUnionToAThreeWayMerge()
    {
        var loadout = await TwoMods(Msg);
        Sources.Index(loadout).Lookup(Msg)!.Strategy.Should().Be(MergeStrategy.FmgUnion);

        WriteGameArchive((Msg, Vanilla));

        Sources.Index(Refresh(loadout)).Lookup(Msg)!.Strategy.Should().Be(MergeStrategy.FmgThreeWay);
    }

    [Fact]
    public async Task AGameArchiveIndexThatCannotBeParsedOffersNothingAndTheReportSaysWhy()
    {
        File.WriteAllBytes((RealGameFolder / "TestData0.bhd").GetFullPath(), new byte[300]);
        File.WriteAllBytes((RealGameFolder / "TestData0.bdt").GetFullPath(), new byte[300]);
        var loadout = await TwoMods(Talk);

        var candidate = Sources.Index(loadout).Lookup(Talk)!;
        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);

        candidate.Outcome.Reason.Should().Be(MergeOutcome.NoVanillaSource);
        report.Refusal.Should().BeNull();
        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Notes.Should().ContainSingle().Which.Should().Contain("TestData0.bhd could not be read").And.Contain("300 bytes");
    }

    [Fact]
    public async Task AnArchiveThatIndexesAFileButCannotBeReadSuppliesNoVanilla()
    {
        WriteGameArchive((Talk, Vanilla));
        var loadout = await TwoMods(Talk);
        var candidate = Sources.Index(loadout).Lookup(Talk)!;
        candidate.Outcome.Merges.Should().BeTrue("the index finds the file, so the read is what has to fail");
        // The index still lists the file, but the payload it points at is gone.
        File.WriteAllBytes((RealGameFolder / "TestData0.bdt").GetFullPath(), new byte[10]);

        Sources.SingleVanilla(loadout, Talk, candidate.Contributors, MergeSourceMode.Report).Should().BeNull();
    }

    private const string Part = "parts/wp_a_0556.partsbnd.dcx";

    private byte[] Packed(IReadOnlyList<(int Id, string Name, byte[] Data)> members) => PartsbndMods.Bundle(Formats, members);

    private async Task<Loadout.ReadOnly> GlowAndMoveset(IReadOnlyList<(int Id, string Name, byte[] Data)> moveset)
    {
        var loadout = await CreateLoadout();
        foreach (var (name, members) in new[] { (PartsbndMods.Glow, PartsbndMods.GlowMembers()), (PartsbndMods.Moveset, moveset) })
        {
            var archive = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
            await TestArchives.WriteEntries(archive, [(Part, Packed(members))]);
            var registered = await RegisterLocalArchive(archive);
            var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
            await LoadoutManager.InstallItem(registered.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
        }
        return Refresh(loadout);
    }

    [Fact]
    public async Task AnEquipmentPartTheGameArchivesHoldIsMergedMemberByMemberAgainstThatCopy()
    {
        WriteGameArchive((Part, Packed(PartsbndMods.Vanilla())));
        var loadout = await GlowAndMoveset(PartsbndMods.MovesetMembers());

        var plan = ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report);

        plan.Refusal.Should().BeNull();
        var merged = plan.Merged.Should().ContainSingle().Which;
        merged.Path.Should().Be(Part);
        merged.Strategy.Should().Be(MergeStrategy.PartsbndThreeWay);
        var members = PartsbndMods.Read(merged.Bytes, Formats);
        members[100].Should().Equal(PartsbndMods.GlowMembers().Single(m => m.Id == 100).Data);
        members[400].Should().Equal(PartsbndMods.MovesetMembers().Single(m => m.Id == 400).Data);
        members[401].Should().Equal(PartsbndMods.MovesetMembers().Single(m => m.Id == 401).Data);
        members[201].Should().Equal(PartsbndMods.Vanilla().Single(m => m.Id == 201).Data);
        // The mod installed last leads the fold, so its model is the one both changed and kept; only the other mod's
        // texture archive is a take, the leader's own members need no note.
        members[200].Should().Equal(PartsbndMods.MovesetMembers().Single(m => m.Id == 200).Data);
        merged.Notes.OfType<MemberKept>().Should().ContainSingle().Which.Should()
            .Match<MemberKept>(k => k.Member == PartsbndMods.FlverName && k.KeptMod.StartsWith(PartsbndMods.Moveset) && k.DroppedMod.StartsWith(PartsbndMods.Glow));
        merged.Notes.OfType<MemberTaken>().Should().ContainSingle().Which.Should()
            .Match<MemberTaken>(t => t.Member == PartsbndMods.TpfName && t.FromMod.StartsWith(PartsbndMods.Glow));
        merged.Notes.Should().Contain(new VanillaServedFromGameArchive(Part));
        MergeReports.SourceOf(merged.Notes).Should().Contain(Part).And.Contain("game's own archives");
    }

    [Fact]
    public async Task TheReportListsTheTakenMembersAndTheStrategyOfAMergedEquipmentPart()
    {
        WriteGameArchive((Part, Packed(PartsbndMods.Vanilla())));
        var loadout = await GlowAndMoveset(PartsbndMods.MovesetMembers());

        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be(Part);
        entry.Strategy.Should().Be("partsbnd-3way");
        entry.Outcome.Should().Be(MergeReportOutcome.Merged);
        entry.Notes.Should().Contain(n => n.Contains(PartsbndMods.TpfName) && n.Contains("only " + PartsbndMods.Glow));
    }

    [Fact]
    public async Task EquipmentPartsWithDifferentMembersEndUnmergedAsTheWinnerWithTheReason()
    {
        WriteGameArchive((Part, Packed(PartsbndMods.Vanilla())));
        var loadout = await GlowAndMoveset([.. PartsbndMods.MovesetMembers().Where(m => m.Id != 201)]);

        var plan = ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report);

        plan.Refusal.Should().BeNull();
        plan.Merged.Should().BeEmpty();
        var unmerged = plan.Unmerged.Should().ContainSingle().Which;
        unmerged.Path.Should().Be(Part);
        unmerged.Outcome.Reason.Should().StartWith("the merge failed: ").And.Contain("the three bundles hold different entries");
        unmerged.Winner!.Value.ModName.Should().StartWith(PartsbndMods.Moveset, "the mod installed last wins by priority");
    }

    [Fact]
    public async Task AnEquipmentPartTheGameArchivesDoNotHoldIsNotMergedAndKeepsTheWinner()
    {
        var loadout = await GlowAndMoveset(PartsbndMods.MovesetMembers());

        var candidate = Sources.Index(loadout).Lookup(Part)!;

        candidate.Strategy.Should().BeNull();
        candidate.Outcome.Merges.Should().BeFalse();
    }
}
