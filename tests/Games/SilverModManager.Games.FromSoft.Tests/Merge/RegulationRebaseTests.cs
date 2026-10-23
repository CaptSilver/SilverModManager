using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Loadouts;
using FormatRegulation = NexusMods.Games.FromSoft.Formats.Regulation;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// The checks around folding mod regulations onto the installed game's: the layout gate before the fold, the rebase
/// verifier after it, over the planner with real regulations and injected counters.
/// <para>
/// Ports of the erm tests prepare_rebase_is_empty_when_the_ancestor_is_the_installed_build,
/// prepare_rebase_offers_the_adopted_baseline_for_an_older_ancestor and prepare_rebase_stops_the_apply_when_a_layout_moved.
/// </para>
/// </summary>
public class RegulationRebaseTests
{
    private const string Path = "regulation.bin";
    private const string Installed = "11701000";
    private const string Older = "11601000";
    private const string SpType = "SP_EFFECT_PARAM_ST";

    private static FromSoftFormatTraits ER => EldenRingGame.Definition.Formats;

    private static byte[] Regulation(string version, int stride, params (int Id, byte Fill)[] rows)
        => RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = (SpType, rows.ToDictionary(r => r.Id, r => Enumerable.Repeat(r.Fill, stride).ToArray()), stride),
        }, version, ER);

    private static byte[] Regulation(string version, params (int Id, byte Fill)[] rows) => Regulation(version, 8, rows);

    private static Dictionary<int, byte[]> RowsOf(byte[] regulation)
        => Param.Read(FormatRegulation.Entries(regulation, ER).Single().Data).Rows.ToDictionary(r => r.Id, r => r.Data);

    private static ProviderFile File(string package, ulong priority) => new(package, Path, Hash.From((ulong)package.GetHashCode()), priority, package);

    private sealed class Counted
    {
        public int Gates;
        public int Verifies;
        public int Identified;
    }

    // Folds the high mod over the low one on top of the game's file, with the real checks counted.
    private static MergePlan Plan(
        Counted counted, byte[] game, byte[] ancestor, byte[] high, byte[] low, Func<MergeStrategy, MergeFold>? bindings = null)
    {
        var files = new[] { File("low", 1), File("high", 2) };
        var contents = new Dictionary<string, byte[]> { ["high"] = high, ["low"] = low };
        var live = new BuildIdentity("1.0", "1", Installed);
        var real = RegulationRebase.For(f => FormatRegulation.Version(FormatRegulation.Unpack(contents[f.PackageId], ER)), () => { counted.Identified++; return live; }, ER);
        var rebase = real with
        {
            Gate = (baseline, mods) => { counted.Gates++; return real.Gate(baseline, mods); },
            Verify = (merged, baseline, mods) => { counted.Verifies++; return real.Verify(merged, baseline, mods); },
        };
        return MergePlanner.Plan(
            MergePathIndex.Build(files, ER, new HashSet<string>()), f => contents[f.PackageId], (_, _) => ancestor, _ => game, ER,
            bindings ?? MergeStrategyBindings.For, rebase);
    }

    [Fact]
    public void AtTheInstalledVersionNeitherTheGateNorTheVerifierRuns()
    {
        var game = Regulation(Installed, (1, 0), (2, 0), (3, 0));
        var counted = new Counted();

        var plan = Plan(counted, game, game, Regulation(Installed, (1, 0xaa), (2, 0), (3, 0)), Regulation(Installed, (1, 0), (2, 0xcc), (3, 0)));

        plan.Refusal.Should().BeNull();
        counted.Gates.Should().Be(0);
        counted.Verifies.Should().Be(0);
        counted.Identified.Should().Be(0, "the game's own regulation says it is at the mods' version, so the executable and Steam's build id are never read");
    }

    [Fact]
    public void AnOlderAgreedVersionFoldsOntoTheGamesFileAndTheOutputClaimsTheInstalledBuild()
    {
        var ancestor = Regulation(Older, (1, 0), (2, 0), (3, 0));
        var game = Regulation(Installed, (1, 0), (2, 0), (3, 0), (4, 0x44));
        var counted = new Counted();

        var plan = Plan(counted, game, ancestor, Regulation(Older, (1, 0xaa), (2, 0), (3, 0)), Regulation(Older, (1, 0), (2, 0xcc), (3, 0)));

        plan.Refusal.Should().BeNull();
        counted.Gates.Should().Be(1);
        counted.Verifies.Should().Be(1);
        var merged = plan.Merged.Single().Bytes;
        FormatRegulation.Version(FormatRegulation.Unpack(merged, ER)).Should().Be(Installed);
        var rows = RowsOf(merged);
        rows[1].Should().Equal(Enumerable.Repeat((byte)0xaa, 8));
        rows[2].Should().Equal(Enumerable.Repeat((byte)0xcc, 8));
        rows[4].Should().Equal(Enumerable.Repeat((byte)0x44, 8), "the patch's own row is kept");
    }

    [Fact]
    public void AGameRegulationWhoseStrideMovedRefusesWithTheGatesSentenceBeforeAnythingIsFolded()
    {
        var ancestor = Regulation(Older, (1, 0), (2, 0), (3, 0));
        var game = Regulation(Installed, 12, (1, 0), (2, 0), (3, 0));
        var counted = new Counted();
        var folded = 0;
        MergeFold fold = (_, _, _, _, _, _, _, _, _) => { folded++; return []; };

        var plan = Plan(counted, game, ancestor, Regulation(Older, (1, 0xaa), (2, 0), (3, 0)), Regulation(Older, (1, 0), (2, 0xcc), (3, 0)), _ => fold);

        var refusal = plan.Refusal.Should().NotBeNull().And.Subject.As<MergeRefusal>();
        refusal.Path.Should().Be(Path);
        refusal.Reason.Should().StartWith("the game's param layout moved, so mod rows can't be transplanted onto it:\n  ");
        refusal.Reason.Should().Contain("high: SP_EFFECT_PARAM_ST.param row stride 8 != baseline 12").And.Contain("low: SP_EFFECT_PARAM_ST.param row stride 8 != baseline 12");
        refusal.Reason.Should().EndWith("\nThose mods need updates built for this game version.");
        folded.Should().Be(0);
        counted.Verifies.Should().Be(0);
        plan.Merged.Should().BeEmpty();
    }

    [Fact]
    public void AFoldThatLostARowAModAuthoredIsRefusedWithTheVerifiersMessage()
    {
        var ancestor = Regulation(Older, (1, 0), (2, 0), (3, 0));
        var game = Regulation(Installed, (1, 0), (2, 0), (3, 0));
        var counted = new Counted();
        // Keeps the game's file as it was handed in, so every row a mod authored is lost.
        MergeFold dropsEverything = (acc, _, _, _, _, _, _, _, _) => acc.ToArray();

        var plan = Plan(counted, game, ancestor, Regulation(Older, (1, 0xaa), (2, 0), (3, 0)), Regulation(Older, (1, 0), (2, 0xcc), (3, 0)), _ => dropsEverything);

        var refusal = plan.Refusal.Should().NotBeNull().And.Subject.As<MergeRefusal>();
        refusal.Reason.Should().StartWith($"the merged {Path} is not a faithful rebase onto the installed build:");
        refusal.Reason.Should().Contain("high: SP_EFFECT_PARAM_ST.param row 1 did not survive the rebase")
            .And.Contain("low: SP_EFFECT_PARAM_ST.param row 2 did not survive the rebase");
        plan.Merged.Should().BeEmpty();
    }
}

/// <summary>The rebase checks and the tampered-install refusal over a real loadout, through the plan cache and the apply.</summary>
public class RegulationRebaseLoadoutTests(ITestOutputHelper helper) : AVanillaDumpTest<RegulationRebaseLoadoutTests>(helper)
{
    private const string RegulationPath = "regulation.bin";

    private readonly Dictionary<Hash, int> _reads = new();

    private MergePlanCache Plans => ServiceProvider.GetRequiredService<MergePlanCache>();

    private MergeDiagnosticsEmitter Emitter => ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<MergeDiagnosticsEmitter>().Single();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<LoadoutMergeSources>(provider => new CountingSources(
            _reads,
            provider.GetRequiredService<IFileStore>(),
            provider.GetRequiredService<RegulationBaselines>(),
            provider.GetRequiredService<VanillaDumps>(),
            provider.GetRequiredService<InstalledBuilds>(),
            provider.GetRequiredService<ILogger<LoadoutMergeSources>>()));
        return services;
    }

    private sealed class CountingSources(Dictionary<Hash, int> reads, IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
        : LoadoutMergeSources(fileStore, baselines, dumps, builds, logger)
    {
        public override byte[] ContributorBytes(ProviderFile file)
        {
            lock (reads) reads[file.Hash] = reads.GetValueOrDefault(file.Hash) + 1;
            return base.ContributorBytes(file);
        }
    }

    private readonly List<LoadoutItemGroup.ReadOnly> _groups = [];

    private async Task<LoadoutItemGroup.ReadOnly> InstallGroup(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var archive = await AddArchive(name, files);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
        return result.LoadoutItemGroup!.Value;
    }

    private async Task<Loadout.ReadOnly> TwoRegulations(string version)
    {
        var loadout = await CreateLoadout();
        _groups.Add(await InstallGroup(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(version, 0x10))));
        _groups.Add(await InstallGroup(loadout.LoadoutId, "Beta", (RegulationPath, Regulation(version, 0x20))));
        return Refresh(loadout);
    }

    private async Task StampAsTampered(Loadout.ReadOnly loadout)
    {
        var live = BuildIdentity.Identify(GameInstallation);
        await ServiceProvider.GetRequiredService<BuildStampStore>().Record(loadout.InstallationId, live with { RegulationHash = new string('0', 64) });
    }

    [Fact]
    public async Task AVersionWithNoBaselineRefusesNamingTheVersion()
    {
        var loadout = await TwoRegulations(OlderVersion);

        var plan = Plans.Current(loadout, MergeSourceMode.Report);

        plan.Refusal.Should().NotBeNull();
        plan.Refusal!.Reason.Should().Contain(OlderVersion);
    }

    [Fact]
    public async Task TheContributorsVersionIsReadOncePerHashAcrossTwoPlans()
    {
        var loadout = await TwoRegulations(GameVersion);
        // The fixture regulations don't share the game's entries, so the merge itself refuses; it still reads both files.
        Plans.Current(loadout, MergeSourceMode.Report);
        var hashes = ServiceProvider.GetRequiredService<LoadoutMergeSources>().Providers(loadout).Select(p => p.Hash).ToArray();
        var afterFirst = hashes.ToDictionary(h => h, h => _reads[h]);

        // Swapping the mods' priority changes the plan's inputs, so the second plan is made again rather than served from the cache.
        var rows = _groups.Select(g => LoadoutItemGroupPriority.FindByTarget(Connection.Db, g.LoadoutItemGroupId).Single()).ToArray();
        await LoadoutManager.ResolveFileConflicts([rows[0].LoadoutItemGroupPriorityId], rows[1].LoadoutItemGroupPriorityId);
        Plans.Current(Refresh(loadout), MergeSourceMode.Report);

        foreach (var hash in hashes)
            (_reads[hash] - afterFirst[hash]).Should().Be(1, "only the merge reads the file again; its build stamp is remembered");
    }

    [Fact]
    public async Task ATamperedInstallRefusesTheRegulationMergeWithTheTamperedText()
    {
        var loadout = await TwoRegulations(GameVersion);
        await StampAsTampered(loadout);
        var expected = HealPlan.TamperedRefusal(BuildIdentity.Identify(GameInstallation), ((IFromSoftGame)GameInstallation.Game).Formats);

        var plan = Plans.Current(loadout, MergeSourceMode.Report);

        plan.Merged.Should().BeEmpty();
        plan.Refusal!.Reason.Should().Be(expected);
        expected.Should().Contain("changed but the game was not patched");
    }

    [Fact]
    public async Task ATamperedInstallRefusesTheApplyBeforeAnythingIsWritten()
    {
        var loadout = await TwoRegulations(GameVersion);
        await StampAsTampered(loadout);
        var mods = GameInstallation.Locations[NexusMods.Sdk.Games.LocationId.Me3Mods].Path;
        var disk = mods.DirectoryExists() ? TreeSnapshot.Take(mods) : [];

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var thrown = await act.Should().ThrowAsync<MergeRefusedException>();
        thrown.Which.Message.Should().Contain("changed but the game was not patched");
        (mods.DirectoryExists() ? TreeSnapshot.Take(mods) : []).Should().Equal(disk);
        (DataRoot / "baselines").DirectoryExists().Should().BeFalse("a tampered regulation is not kept as a baseline");
    }

    [Fact]
    public async Task HealthCheckShowsTheTamperedRefusalInTheSameWords()
    {
        var loadout = await TwoRegulations(GameVersion);
        await StampAsTampered(loadout);
        var expected = HealPlan.TamperedRefusal(BuildIdentity.Identify(GameInstallation), ((IFromSoftGame)GameInstallation.Game).Formats);

        var diagnostics = new List<Diagnostic>();
        await foreach (var diagnostic in Emitter.Diagnose(Refresh(loadout), CancellationToken.None)) diagnostics.Add(diagnostic);

        var diagnostic1 = diagnostics.Should().ContainSingle().Which;
        diagnostic1.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic1.Should().BeOfType<Diagnostic<FromSoftDiagnostics.MergeRefusedMessageData>>().Which.MessageData.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task AnUnstampedInstallIsNotTreatedAsTampered()
    {
        var loadout = await TwoRegulations(GameVersion);

        var plan = Plans.Current(loadout, MergeSourceMode.Report);

        // The fixture regulations don't share the game's entries, so the merge itself refuses, for that reason.
        plan.Refusal!.Reason.Should().Contain("different entries").And.NotContain("not patched");
    }
}
