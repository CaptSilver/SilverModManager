using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class LoadoutMergeSourcesTests(ITestOutputHelper helper) : AModdedEldenRingTest<LoadoutMergeSourcesTests>(helper)
{
    private const string GameVersion = EldenRingInstall.RegulationVersion;
    private const string OlderVersion = "11601000";
    private const string OldestVersion = "11602000";

    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    private AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    private string[] DataRootListing() => Directory.Exists(DataRoot.GetFullPath()) ? TreeSnapshot.Take(DataRoot) : [];

    private static byte[] Regulation(string version, byte fill)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], version, Formats);

    // A regulation with one real param table of rows 'ids', each filled with its own id, so rows can be told apart.
    private static byte[] RealRegulation(string version, int[] ids, int? edited = null, int? alsoEdited = null)
        => RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = ("SP_EFFECT_PARAM_ST", ids.ToDictionary(id => id, id => Enumerable.Repeat((byte)(id == edited || id == alsoEdited ? 0xEE : id), 8).ToArray()), 8),
        }, version, Formats);

    private async Task<LoadoutItemGroup.ReadOnly> InstallPackage(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
        => await InstallPackage(loadout, default, name, files);

    private async Task<LoadoutItemGroup.ReadOnly> InstallPackage(LoadoutId loadout, Optional<LoadoutItemGroupId> parent, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, parent, installer);
        return result.LoadoutItemGroup!.Value;
    }

    private static (string Path, byte[] Content) Member(string path) => (path, System.Text.Encoding.UTF8.GetBytes(path));

    private ConflictPriority PriorityOf(LoadoutItemGroup.ReadOnly group)
        => LoadoutItemGroupPriority.FindByTarget(Connection.Db, group.LoadoutItemGroupId).Single().Priority;

    private async Task<(Loadout.ReadOnly Loadout, IReadOnlyList<ProviderFile> Regulations)> TwoRegulations(string firstVersion, byte[] first, byte[] second)
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", ("regulation.bin", first));
        await InstallPackage(loadout.LoadoutId, "Beta", ("regulation.bin", second));
        var providers = Sources.Providers(Refresh(loadout)).Where(p => p.RelPath == "regulation.bin").ToArray();
        providers.Should().HaveCount(2, $"both regulations are version {firstVersion} or another one, and both are providers");
        return (Refresh(loadout), providers);
    }

    private void KeepBaseline(string version, byte[] bytes)
    {
        var folder = DataRoot / "baselines";
        folder.CreateDirectory();
        File.WriteAllBytes((folder / $"regulation-{version}.bin").GetFullPath(), bytes);
    }

    [Fact]
    public async Task ProvidersNameBothPackagesWithDistinctPriorities()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Weapons", Member("parts/wp_a.dcx"));
        await InstallPackage(loadout.LoadoutId, "Armor", Member("parts/wp_a.dcx"));

        var providers = Sources.Providers(Refresh(loadout));

        providers.Select(p => p.PackageId).Should().BeEquivalentTo("Weapons", "Armor");
        providers.Select(p => p.RelPath).Should().OnlyContain(p => p == "parts/wp_a.dcx");
        providers.Select(p => p.Priority).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task TheLoadoutsModPriorityDecidesTheContributorOrder()
    {
        var loadout = await CreateLoadout();
        var weapons = await InstallPackage(loadout.LoadoutId, "Weapons", Member("parts/wp_a.dcx"));
        var armor = await InstallPackage(loadout.LoadoutId, "Armor", Member("parts/wp_a.dcx"));

        Sources.Index(Refresh(loadout)).Lookup("parts/wp_a.dcx")!.Contributors.Select(c => c.PackageId)
            .Should().Equal(["Armor", "Weapons"], "the mod installed last starts out on top");

        var weaponsRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, weapons.LoadoutItemGroupId).Single();
        var armorRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, armor.LoadoutItemGroupId).Single();
        await LoadoutManager.ResolveFileConflicts([weaponsRow.LoadoutItemGroupPriorityId], armorRow.LoadoutItemGroupPriorityId);

        var after = Sources.Index(Refresh(loadout)).Lookup("parts/wp_a.dcx")!;
        after.Contributors.Select(c => c.PackageId).Should().Equal(["Weapons", "Armor"]);
        after.Contributors[0].Priority.Should().BeGreaterThan(after.Contributors[1].Priority);
    }

    [Fact]
    public async Task AModInstalledInsideACollectionCarriesTheModsPriorityAndName()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout.LoadoutId, "Collection");
        await InstallPackage(loadout.LoadoutId, "Other", Member("parts/wp_a.dcx"));
        var inside = await InstallPackage(loadout.LoadoutId, collection.AsLoadoutItemGroup().LoadoutItemGroupId, "Weapons", Member("parts/wp_a.dcx"));

        var weapons = Sources.Providers(Refresh(loadout)).Single(p => p.PackageId == "Weapons");

        weapons.Priority.Should().Be(PriorityOf(inside).Value);
        weapons.ModName.Should().Be(Refresh(inside).AsLoadoutItem().Name);
    }

    [Fact]
    public async Task ADisabledPackageIsNotAProvider()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Weapons", Member("parts/wp_a.dcx"));
        var armor = await InstallPackage(loadout.LoadoutId, "Armor", Member("parts/wp_a.dcx"));
        await DisableItem(armor.Id);

        var providers = Sources.Providers(Refresh(loadout));

        providers.Select(p => p.PackageId).Should().Equal("Weapons");
        Sources.Index(Refresh(loadout)).Candidates.Should().BeEmpty("one provider left is nothing to merge");
    }

    [Fact]
    public async Task ContributorBytesAreTheArchiveMemberNotTheArchive()
    {
        var loadout = await CreateLoadout();
        var member = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        await InstallPackage(loadout.LoadoutId, "Weapons", ("msg/engus/item.msgbnd.dcx", member));

        var provider = Sources.Providers(Refresh(loadout)).Single();

        Sources.ContributorBytes(provider).Should().Equal(member);
    }

    [Fact]
    public async Task TheVanillaRegulationComesFromTheKeptBaselineOfTheContributorsVersion()
    {
        var baseline = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, baseline);
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));

        var vanilla = Sources.SingleVanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        vanilla.Should().Equal(baseline);
        FromSoft.Formats.Regulation.Version(FromSoft.Formats.Regulation.Unpack(vanilla!, Formats)).Should().Be(OlderVersion);
    }

    [Fact]
    public async Task ABaselineKeptUnderTheContributorsVersionButStampedWithAnotherIsRefused()
    {
        KeepBaseline(OlderVersion, Regulation(GameVersion, 0xB0));
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));

        var act = () => Sources.SingleVanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Message
            .Should().Contain($"kept for version {OlderVersion} is version {GameVersion}");
    }

    [Fact]
    public async Task ReportModeReadsTheLiveRegulationAndWritesNothing()
    {
        var (loadout, providers) = await TwoRegulations(GameVersion, Regulation(GameVersion, 1), Regulation(GameVersion, 2));
        var rootBefore = DataRootListing();
        var gameBefore = SnapshotOfGameFolder();
        var notes = new List<IMergeNote>();

        var vanilla = Sources.SingleVanilla(loadout, "regulation.bin", providers, MergeSourceMode.Report, notes);

        vanilla.Should().Equal(EldenRingInstall.RegulationFile);
        notes.Should().ContainSingle().Which.Describe().Should().Contain("next apply keeps").And.Contain(GameVersion);
        DataRootListing().Should().Equal(rootBefore);
        SnapshotOfGameFolder().Should().Equal(gameBefore);
    }

    [Fact]
    public async Task ApplyModeAdoptsTheLiveRegulationAndReadsTheKeptCopy()
    {
        var (loadout, providers) = await TwoRegulations(GameVersion, Regulation(GameVersion, 1), Regulation(GameVersion, 2));
        var gameBefore = SnapshotOfGameFolder();

        var vanilla = Sources.SingleVanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        vanilla.Should().Equal(EldenRingInstall.RegulationFile);
        File.ReadAllBytes((DataRoot / "baselines" / $"regulation-{GameVersion}.bin").GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
        SnapshotOfGameFolder().Should().Equal(gameBefore);
    }

    [Fact]
    public async Task ContributorsOfDifferentVersionsEachGetTheVanillaOfTheirVersion()
    {
        var olderBaseline = Regulation(OlderVersion, 0xB0);
        var oldestBaseline = Regulation(OldestVersion, 0xB1);
        KeepBaseline(OlderVersion, olderBaseline);
        KeepBaseline(OldestVersion, oldestBaseline);
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OldestVersion, 2));
        var notes = new List<IMergeNote>();

        var ancestors = Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply, notes)!;

        var alpha = providers.Single(p => p.PackageId == "Alpha");
        var beta = providers.Single(p => p.PackageId == "Beta");
        ancestors.Of(alpha).Should().Equal(olderBaseline);
        ancestors.Of(beta).Should().Equal(oldestBaseline);
        ancestors.Groups.Select(g => g.Version).Should().BeEquivalentTo([OlderVersion, OldestVersion]);
        notes.OfType<VanillaGroupSource>().Should().HaveCount(2)
            .And.Contain(n => n.Version == OlderVersion && n.Mods.Contains(alpha.ModName))
            .And.Contain(n => n.Version == OldestVersion && n.Mods.Contains(beta.ModName));
    }

    [Fact]
    public async Task VanillaGroupsNameEachBuildItsModsAndWhereItsVanillaCameFromWithoutRefusingTheOthers()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OldestVersion, 2));
        var rootBefore = DataRootListing();
        var alpha = providers.Single(p => p.PackageId == "Alpha");
        var beta = providers.Single(p => p.PackageId == "Beta");

        var groups = Sources.VanillaGroups(loadout);

        groups.Select(g => g.Version).Should().BeEquivalentTo([OlderVersion, OldestVersion]);
        var older = groups.Single(g => g.Version == OlderVersion);
        older.Mods.Should().Equal(alpha.ModName);
        older.Source.Should().NotBeNull().And.Contain("vanilla");
        older.Tried.Should().BeEmpty();
        var oldest = groups.Single(g => g.Version == OldestVersion);
        oldest.Mods.Should().Equal(beta.ModName);
        oldest.Source.Should().BeNull();
        oldest.Tried.Should().NotBeEmpty();
        DataRootListing().Should().Equal(rootBefore, "listing the groups is a read");
    }

    [Fact]
    public async Task VanillaGroupsOfALoadoutWithoutRegulationsIsEmpty()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Weapons", Member("parts/wp_a.dcx"));

        Sources.VanillaGroups(Refresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ContributorsOfOneVersionShareOneGroupAndNoGroupNote()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));
        var notes = new List<IMergeNote>();

        var ancestors = Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply, notes)!;

        ancestors.Groups.Should().ContainSingle().Which.Contributors.Should().BeEquivalentTo(providers);
        notes.OfType<VanillaGroupSource>().Should().BeEmpty("one group needs no label to say whose it is");
    }

    [Fact]
    public async Task AModAtTheGamesBuildIsComparedAgainstTheInstalledFileWhileAnOlderOneUsesItsBaseline()
    {
        var olderBaseline = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, olderBaseline);
        var (loadout, providers) = await TwoRegulations(GameVersion, Regulation(GameVersion, 1), Regulation(OlderVersion, 2));

        var ancestors = Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply)!;

        ancestors.Of(providers.Single(p => p.PackageId == "Alpha")).Should().Equal(EldenRingInstall.RegulationFile);
        ancestors.Of(providers.Single(p => p.PackageId == "Beta")).Should().Equal(olderBaseline);
    }

    [Fact]
    public async Task AGroupWithNoVanillaIsRefusedNamingItsModsItsVersionAndWhatWasTried()
    {
        var (loadout, providers) = await TwoRegulations(GameVersion, Regulation(GameVersion, 1), Regulation(OlderVersion, 2));
        var beta = providers.Single(p => p.PackageId == "Beta");

        var act = () => Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        var missing = act.Should().Throw<MergeException>().Which.Missing.Should().ContainSingle("the game's own group is covered by the installed file").Which;
        missing.Version.Should().Be(OlderVersion);
        missing.Mods.Should().Equal(beta.ModName);
        missing.Tried.Should().NotBeEmpty().And.Contain(t => t.Contains("installed game") && t.Contains(GameVersion));
    }

    [Fact]
    public async Task EveryGroupWithNoVanillaIsListedInOneRefusal()
    {
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OldestVersion, 2));

        var act = () => Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        var thrown = act.Should().Throw<MergeException>().Which;
        thrown.Missing.Select(m => m.Version).Should().BeEquivalentTo([OlderVersion, OldestVersion]);
        thrown.Message.Should().Contain("Alpha").And.Contain(OlderVersion).And.Contain("Beta").And.Contain(OldestVersion);
    }

    [Fact]
    public async Task OfTwoVouchedFilesOfOneBuildTheOneNearestTheModsIsUsedAndSaid()
    {
        var mod = RealRegulation(OlderVersion, [1, 2, 3, 4], edited: 4);
        var far = RealRegulation(OlderVersion, [1, 2, 3, 4], edited: 1, alsoEdited: 2);
        var near = RealRegulation(OlderVersion, [1, 2, 3, 4]);
        var folder = DataRoot / "baselines";
        folder.CreateDirectory();
        // The primary name is the far one, so picking the first file found would be wrong.
        File.WriteAllBytes((folder / $"regulation-{OlderVersion}.bin").GetFullPath(), far);
        File.WriteAllBytes((folder / $"regulation-{OlderVersion}@near.bin").GetFullPath(), near);
        var (loadout, providers) = await TwoRegulations(OlderVersion, mod, mod);
        var notes = new List<IMergeNote>();

        var ancestors = Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply, notes)!;

        ancestors.Groups.Single().Bytes.Should().Equal(near);
        notes.OfType<VanillaAlternateChosen>().Should().ContainSingle().Which.Should().Match<VanillaAlternateChosen>(n => n.Version == OlderVersion && n.Of == 2);
    }

    [Fact]
    public async Task AModWhoseRegulationCarriesAStampButNoReadableBinderIsRefusedWhenChoosingBetweenVouchedFiles()
    {
        var folder = DataRoot / "baselines";
        folder.CreateDirectory();
        File.WriteAllBytes((folder / $"regulation-{OlderVersion}.bin").GetFullPath(), RealRegulation(OlderVersion, [1, 2]));
        File.WriteAllBytes((folder / $"regulation-{OlderVersion}@other.bin").GetFullPath(), RealRegulation(OlderVersion, [1, 2], edited: 1));
        // The header up to the stamp survives, so the version reads, but the binder after it is cut off.
        var payload = FromSoft.Formats.Regulation.Unpack(RealRegulation(OlderVersion, [1, 2]), Formats);
        var broken = FromSoft.Formats.Regulation.Pack(payload.AsSpan(0, 0x40), new byte[FromSoft.Formats.Regulation.IvSize], Formats);
        var (loadout, providers) = await TwoRegulations(OlderVersion, broken, RealRegulation(OlderVersion, [1, 2]));

        var act = () => Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Message.Should().Contain(OlderVersion);
    }

    [Fact]
    public async Task AVersionTheGameDoesNotHaveAndNoBaselineIsRefusedNamingBothVersions()
    {
        // The game's own version is kept as a baseline, which must not be mistaken for the contributors' version.
        KeepBaseline(GameVersion, EldenRingInstall.RegulationFile);
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));
        var gameBefore = SnapshotOfGameFolder();

        var act = () => Sources.SingleVanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Message
            .Should().Contain($"no vanilla regulation of version {OlderVersion} is available").And.Contain(GameVersion);
        SnapshotOfGameFolder().Should().Equal(gameBefore);
    }

    [Fact]
    public async Task AFileOtherThanTheRegulationHasNoVanillaWithoutADump()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Weapons", Member("msg/engus/item.msgbnd.dcx"));
        await InstallPackage(loadout.LoadoutId, "Armor", Member("msg/engus/item.msgbnd.dcx"));
        var providers = Sources.Providers(Refresh(loadout));

        Sources.SingleVanilla(Refresh(loadout), "msg/engus/item.msgbnd.dcx", providers, MergeSourceMode.Apply).Should().BeNull();
    }

    [Fact]
    public void TheGameBaseIsTheLooseRegulationInTheGameFolder()
    {
        var before = SnapshotOfGameFolder();

        Sources.GameBase(GameInstallation, "regulation.bin").Should().Equal(EldenRingInstall.RegulationFile);

        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public void AGameBaseThatIsNotInTheGameFolderIsRefusedNamingThePath()
    {
        var act = () => Sources.GameBase(GameInstallation, "param/missing.bin");

        act.Should().Throw<MergeException>().Which.Message.Should().Contain("param/missing.bin");
    }
}
