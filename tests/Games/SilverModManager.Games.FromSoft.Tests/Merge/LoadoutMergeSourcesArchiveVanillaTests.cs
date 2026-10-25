using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using FormatRegulation = SilverModManager.Games.FromSoft.Formats.Regulation;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// A vanilla regulation that only sits inside a library archive (a randomizer bundles the one it works from) serves as
/// the ancestor of the mods built for its build, once it passes the checks of a vanilla. Nothing outside the file
/// vouches for it, so it is kept in the validated tier.
/// </summary>
public class LoadoutMergeSourcesArchiveVanillaTests(ITestOutputHelper helper) : AVanillaDumpTest<LoadoutMergeSourcesArchiveVanillaTests>(helper)
{
    private const string Member = "randomizer/diste/Vanilla/regulation.bin";
    private const string SpType = "SP_EFFECT_PARAM_ST";

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    // What the Steam manifests list in these tests: nothing until a test adds a file.
    private readonly ListedVouching _steam = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<IManifestVouching>(_steam);
        return services;
    }

    private sealed class ListedVouching : IManifestVouching
    {
        public HashSet<Sha1Value> Listed { get; } = [];

        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash)
            => Listed.Contains(hash) ? new ManifestMatch(ManifestId.From(42), DepotId.From(1245621)) : null;
    }

    private RegulationBaselines Baselines => ServiceProvider.GetRequiredService<RegulationBaselines>();

    /// <summary>A regulation with one param table of rows 1..rows, each filled with its own id, except the ones edited.</summary>
    private static byte[] Table(string version, int rows = 60, int stride = 8, params int[] edited)
        => RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = (SpType, Enumerable.Range(1, rows).ToDictionary(id => id, id => Enumerable.Repeat(edited.Contains(id) ? (byte)0xEE : (byte)id, stride).ToArray()), stride),
        }, version, Formats);

    private async Task<(Loadout.ReadOnly Loadout, IReadOnlyList<ProviderFile> Providers)> Mods(params (string Name, byte[] Regulation)[] mods)
    {
        var loadout = await CreateLoadout();
        foreach (var (name, regulation) in mods) await InstallPackage(loadout.LoadoutId, name, ("regulation.bin", regulation));
        loadout = Refresh(loadout);
        return (loadout, Sources.Providers(loadout));
    }

    private string[] BaselineFiles()
    {
        var folder = (DataRoot / "baselines").GetFullPath();
        return Directory.Exists(folder) ? Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray()! : [];
    }

    [Fact]
    public async Task AVanillaInsideALibraryArchiveServesTheModsOfItsBuildAndIsKeptValidated()
    {
        var vanilla = Table(OlderVersion);
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        await AddArchive("Randomizer", (Member, vanilla), ("randomizer/regulation.bin", Table(OlderVersion, edited: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25])));
        var notes = new List<IMergeNote>();

        var ancestors = Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply, notes)!;

        ancestors.Groups.Should().ContainSingle().Which.Bytes.Should().Equal(vanilla);
        BaselineFiles().Should().Contain($"regulation-{OlderVersion}.bin");
        var provenance = Baselines.Provenance(OlderVersion)!;
        provenance.Tier.Should().Be(BaselineTier.Validated);
        provenance.Source.Should().Be(BaselineSource.LibraryArchive);
        provenance.Describe().Should().Contain("Randomizer.zip").And.Contain(Member);
        Baselines.Known().Should().BeEmpty("a file only validated never vouches for anything");
    }

    [Fact]
    public async Task AReadOnlyLookupUsesTheArchiveVanillaAndKeepsNothing()
    {
        var vanilla = Table(OlderVersion);
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        await AddArchive("Randomizer", (Member, vanilla));
        var notes = new List<IMergeNote>();

        var ancestors = Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Report, notes)!;

        ancestors.Groups.Single().Bytes.Should().Equal(vanilla);
        BaselineFiles().Should().BeEmpty();
        notes.Should().Contain(new BaselineWillBeKept(OlderVersion));
    }

    [Fact]
    public async Task OnceKeptTheArchiveVanillaIsServedFromTheBaselineAndCountsAsValidated()
    {
        var vanilla = Table(OlderVersion);
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        var archive = await AddArchive("Randomizer", (Member, vanilla));
        Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply);
        var notes = new List<IMergeNote>();

        var again = Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Report, notes)!;

        again.Groups.Single().Bytes.Should().Equal(vanilla);
        notes.Should().NotContain(n => n is BaselineWillBeKept);
        ServiceProvider.GetRequiredService<VanillaDumps>().Find(Connection.Db, GameInstallation).Should().BeEmpty("a validated file does not make its archive a vanilla dump");
        archive.Should().NotBeNull();
    }

    [Fact]
    public async Task ModsOfTwoBuildsEachGetTheirOwnVanillaOneFromAnArchiveOneFromTheGame()
    {
        var vanilla = Table(OlderVersion);
        var (loadout, providers) = await Mods(
            ("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)), ("Gamma", EldenRingInstall.RegulationFile));
        await AddArchive("Randomizer", (Member, vanilla));

        var ancestors = Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply)!;

        ancestors.Groups.Should().HaveCount(2);
        ancestors.Of(providers.Single(p => p.PackageId == "Alpha")).Should().Equal(vanilla);
        ancestors.Of(providers.Single(p => p.PackageId == "Beta")).Should().Equal(vanilla);
        ancestors.Of(providers.Single(p => p.PackageId == "Gamma")).Should().Equal(EldenRingInstall.RegulationFile);
    }

    [Fact]
    public async Task ACandidateCarryingAnEditTwoModsLackIsRefusedNamingTheRow()
    {
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        await AddArchive("Modded", (Member, Table(OlderVersion, edited: 5)));

        var act = () => Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply);

        var missing = act.Should().Throw<MergeException>().Which.Missing.Should().ContainSingle().Which;
        missing.Tried.Should().Contain(t => t.Contains("Modded.zip") && t.Contains("consensus") && t.Contains("row 5"));
        BaselineFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ARegulationRewrittenWholesaleIsRefusedForBeingTooFarFromTheMod()
    {
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)));
        var rewritten = Table(OlderVersion, edited: Enumerable.Range(1, 50).ToArray());
        await AddArchive("Randomized", (Member, rewritten));

        var act = () => Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Missing.Single().Tried.Should().Contain(t => t.Contains("Randomized.zip") && t.Contains("closeness"));
    }

    [Fact]
    public async Task ACandidateWhoseLayoutDisagreesWithTheGameIsRefused()
    {
        File.WriteAllBytes((RealGameFolder / "regulation.bin").GetFullPath(), Table(GameVersion));
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        await AddArchive("Wide", (Member, Table(OlderVersion, stride: 12)));

        var act = () => Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Missing.Single().Tried.Should().Contain(t => t.Contains("Wide.zip") && t.Contains("layout"));
    }

    [Fact]
    public async Task AModsOwnRegulationIsNeverItsVanilla()
    {
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)));

        var act = () => Sources.Ancestors(loadout, "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Missing.Should().ContainSingle();
        BaselineFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveRegulationOfAnotherBuildIsNotAVanillaForTheseMods()
    {
        var (loadout, providers) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));
        await AddArchive("Other", (Member, Table(NewerVersion)));

        var act = () => Sources.Ancestors(Refresh(loadout), "regulation.bin", providers, MergeSourceMode.Apply);

        act.Should().Throw<MergeException>().Which.Missing.Single().Version.Should().Be(OlderVersion);
    }

    [Fact]
    public async Task AScanWithAnotherGamesKeyDoesNotHideTheRegulationFromThisGame()
    {
        // Elden Ring and Armored Core VI both call their regulation regulation.bin; a scan for one game must not
        // remember the other's file as unreadable.
        await AddArchive("Randomizer", (Member, Table(OlderVersion)));
        var archives = ServiceProvider.GetRequiredService<ArchiveVanillas>();
        var otherGame = Formats with { RegulationKey = [.. Enumerable.Repeat((byte)0x5A, 32)] };

        archives.Find(Connection.Db, otherGame, OlderVersion, new HashSet<NexusMods.Hashing.xxHash3.Hash>()).Should().BeEmpty();

        archives.Find(Connection.Db, Formats, OlderVersion, new HashSet<NexusMods.Hashing.xxHash3.Hash>()).Should().ContainSingle().Which.Member.Should().Be(Member);
    }

    // Rows 1..60 filled with their own id, with the given rows filled with another byte instead.
    private static byte[] Edited(string version, params (int Id, byte Fill)[] edits)
        => RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = (SpType, Enumerable.Range(1, 60).ToDictionary(id => id, id => Enumerable.Repeat(edits.Where(e => e.Id == id).Select(e => e.Fill).DefaultIfEmpty((byte)id).First(), 8).ToArray()), 8),
        }, version, Formats);

    private static Dictionary<int, byte> FillOf(byte[] regulation)
        => Param.Read(FormatRegulation.Entries(regulation, Formats).Single().Data).Rows.ToDictionary(r => r.Id, r => r.Data[0]);

    [Fact]
    public async Task FiveModsOfTwoOlderBuildsMergeOntoTheGameWithNoConflictWhenOneBuildsVanillaIsInAnArchive()
    {
        const string midVersion = "11650000";
        File.WriteAllBytes((RealGameFolder / "regulation.bin").GetFullPath(), Edited(GameVersion, (40, 0xA0), (41, 0xA1)));
        KeepBaseline(midVersion, Edited(midVersion));
        var loadout = await CreateLoadout();
        // Three mods of the oldest build; A and C both edit row 50 on the same byte, the way two weapon mods do.
        await InstallPackage(loadout.LoadoutId, "A", ("regulation.bin", Edited(OlderVersion, (3, 0xB3), (50, 0xB5))));
        await InstallPackage(loadout.LoadoutId, "B", ("regulation.bin", Edited(OlderVersion, (9, 0xB9))));
        await InstallPackage(loadout.LoadoutId, "C", ("regulation.bin", Edited(OlderVersion, (50, 0xB5))));
        await InstallPackage(loadout.LoadoutId, "D", ("regulation.bin", Edited(midVersion, (20, 0xC4))));
        await InstallPackage(loadout.LoadoutId, "E", ("regulation.bin", Edited(midVersion, (21, 0xC5))));
        await AddArchive("Randomizer", (Member, Edited(OlderVersion)));

        var plan = ServiceProvider.GetRequiredService<MergePlanCache>().Current(Refresh(loadout), MergeSourceMode.Apply);

        plan.Refusal.Should().BeNull();
        var merged = plan.Merged.Should().ContainSingle().Which;
        var rows = FillOf(merged.Bytes);
        rows.Where(r => r.Value != r.Key).ToDictionary(r => r.Key, r => r.Value).Should().BeEquivalentTo(new Dictionary<int, byte>
        {
            [3] = 0xB3, [9] = 0xB9, [20] = 0xC4, [21] = 0xC5, [40] = 0xA0, [41] = 0xA1, [50] = 0xB5,
        });
        merged.Notes.OfType<RowConflict>().Should().BeEmpty();
        var sources = merged.Notes.OfType<VanillaGroupSource>().ToArray();
        sources.Should().HaveCount(2);
        sources.Single(n => n.Version == OlderVersion).Describe().Should().Contain("validated from a library archive").And.Contain("Randomizer.zip");
        sources.Single(n => n.Version == midVersion).Describe().Should().NotContain("validated");
    }

    private NexusMods.Paths.AbsolutePath Pointed(byte[] bytes)
    {
        var path = TemporaryFileManager.CreateFolder().Path / "mine.bin";
        File.WriteAllBytes(path.GetFullPath(), bytes);
        return path;
    }

    [Fact]
    public async Task AFileTheUserPointsAtIsKeptValidatedWhenItPassesAgainstTheMods()
    {
        var vanilla = Table(OlderVersion);
        var (loadout, _) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));

        var kept = Sources.KeepUserVanilla(loadout, Pointed(vanilla), expectedVersion: null);

        kept.Outcome.Should().Be(ImportOutcome.Kept);
        kept.Provenance.Tier.Should().Be(BaselineTier.Validated);
        kept.Provenance.Source.Should().Be(BaselineSource.UserFile);
        File.ReadAllBytes(kept.File.GetFullPath()).Should().Equal(vanilla);
        Baselines.Known().Should().BeEmpty();
    }

    [Fact]
    public async Task AFileTheUserPointsAtThatSteamListsIsKeptVouchedWithoutCheckingItAgainstTheMods()
    {
        var vanilla = Table(NewerVersion);
        _steam.Listed.Add(Sha1Value.From(System.Security.Cryptography.SHA1.HashData(vanilla)));
        var (loadout, _) = await Mods(("Alpha", Table(OlderVersion, edited: 3)));

        var kept = Sources.KeepUserVanilla(loadout, Pointed(vanilla), expectedVersion: null);

        kept.Provenance.Tier.Should().Be(BaselineTier.Vouched);
        kept.Provenance.Source.Should().Be(BaselineSource.SteamManifest);
        kept.Version.Should().Be(NewerVersion);
    }

    [Fact]
    public async Task AFileTheUserPointsAtIsRefusedWhenNoModWasBuiltForItsBuild()
    {
        var (loadout, _) = await Mods(("Alpha", Table(OlderVersion, edited: 3)));

        var act = () => Sources.KeepUserVanilla(loadout, Pointed(Table(NewerVersion)), expectedVersion: null);

        act.Should().Throw<HealException>().Which.Message.Should().Contain(NewerVersion);
        BaselineFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task AFileTheUserPointsAtIsRefusedWhenItCarriesAnEditTheModsLack()
    {
        var (loadout, _) = await Mods(("Alpha", Table(OlderVersion, edited: 3)), ("Beta", Table(OlderVersion, edited: 9)));

        var act = () => Sources.KeepUserVanilla(loadout, Pointed(Table(OlderVersion, edited: 5)), expectedVersion: null);

        act.Should().Throw<HealException>().Which.Message.Should().Contain("consensus").And.Contain("row 5");
        BaselineFiles().Should().BeEmpty();
    }
}
