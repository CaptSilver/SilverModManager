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

    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    private AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    private string[] DataRootListing() => Directory.Exists(DataRoot.GetFullPath()) ? TreeSnapshot.Take(DataRoot) : [];

    private static byte[] Regulation(string version, byte fill)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], version, Formats);

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

        var vanilla = Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        vanilla.Should().Equal(baseline);
        FromSoft.Formats.Regulation.Version(FromSoft.Formats.Regulation.Unpack(vanilla!, Formats)).Should().Be(OlderVersion);
    }

    [Fact]
    public async Task ABaselineKeptUnderTheContributorsVersionButStampedWithAnotherIsRefused()
    {
        KeepBaseline(OlderVersion, Regulation(GameVersion, 0xB0));
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));

        var act = () => Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

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

        var vanilla = Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Report, notes);

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

        var vanilla = Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        vanilla.Should().Equal(EldenRingInstall.RegulationFile);
        File.ReadAllBytes((DataRoot / "baselines" / $"regulation-{GameVersion}.bin").GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
        SnapshotOfGameFolder().Should().Equal(gameBefore);
    }

    [Fact]
    public async Task ContributorsOfDifferentVersionsAreRefusedNamingBoth()
    {
        var (loadout, providers) = await TwoRegulations(GameVersion, Regulation(GameVersion, 1), Regulation(OlderVersion, 2));

        var act = () => Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Message
            .Should().Contain("Alpha").And.Contain(GameVersion).And.Contain("Beta").And.Contain(OlderVersion);
    }

    [Fact]
    public async Task AVersionTheGameDoesNotHaveAndNoBaselineIsRefusedNamingBothVersions()
    {
        // The game's own version is kept as a baseline, which must not be mistaken for the contributors' version.
        KeepBaseline(GameVersion, EldenRingInstall.RegulationFile);
        var (loadout, providers) = await TwoRegulations(OlderVersion, Regulation(OlderVersion, 1), Regulation(OlderVersion, 2));
        var gameBefore = SnapshotOfGameFolder();

        var act = () => Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

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

        Sources.Vanilla(Refresh(loadout), "msg/engus/item.msgbnd.dcx", providers, MergeSourceMode.Apply).Should().BeNull();
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
