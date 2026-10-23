using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// The merge sources once the library holds vanilla dumps. A dump is vouched for through a kept baseline, the live
/// install, or <see cref="ExtraVouching"/>, which stands in for the game file hash database.
/// </summary>
public class LoadoutMergeSourcesDumpTests(ITestOutputHelper helper) : AVanillaDumpTest<LoadoutMergeSourcesDumpTests>(helper)
{
    private const string TalkPath = "script/talk/m00.talkesdbnd.dcx";
    private const string MsgPath = "msg/engus/item.msgbnd.dcx";

    private readonly HashSet<Hash> _hashDatabase = [];
    private readonly CountingInstalledBuilds _builds = new();

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<InstalledBuilds>(_builds);
        services.AddSingleton<IVouchedRegulations>(provider => new ExtraVouching(provider.GetRequiredService<VouchedRegulations>(), _hashDatabase));
        return services;
    }

    private sealed class ExtraVouching(IVouchedRegulations real, HashSet<Hash> extra) : IVouchedRegulations
    {
        public IReadOnlySet<Hash> Hashes(GameInstallation installation) => real.Hashes(installation).Concat(extra).ToHashSet();
    }

    private sealed class CountingInstalledBuilds : InstalledBuilds
    {
        public int Reads;
        public string? ReportedVersion;

        protected override BuildIdentity Read(GameInstallation installation)
        {
            Reads++;
            var identity = base.Read(installation);
            return ReportedVersion is null ? identity : identity with { RegulationVersion = ReportedVersion };
        }
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private Task AddDump(string name, byte[] regulation, string msg)
        => AddArchive(name, ("Vanilla/regulation.bin", regulation), ("Vanilla/msg/engus/item.msgbnd.dcx", Bytes(msg)), ("Vanilla/m00.talkesdbnd.dcx", Bytes("talk " + msg)));

    private async Task<Loadout.ReadOnly> TwoMods(params (string Path, byte[] Content)[] shared)
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", shared);
        await InstallPackage(loadout.LoadoutId, "Beta", shared);
        return Refresh(loadout);
    }

    [Fact]
    public async Task ATalkesdbndOverlapMergesOnceTheLibraryHoldsADump()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        var loadout = await TwoMods((TalkPath, Bytes("mod")));

        var without = Sources.Index(loadout).Lookup(TalkPath)!;
        without.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        without.Outcome.Merges.Should().BeFalse();
        without.Outcome.Reason.Should().Be(MergeOutcome.NoVanillaSource);

        await AddDump("Dump", regulation, "vanilla");
        loadout = Refresh(loadout);

        var with = Sources.Index(loadout).Lookup(TalkPath)!;
        with.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        with.Outcome.Merges.Should().BeTrue();
        var notes = new List<IMergeNote>();
        Sources.Vanilla(loadout, TalkPath, with.Contributors, MergeSourceMode.Report, notes).Should().Equal(Bytes("talk vanilla"));
        notes.Should().ContainSingle().Which.Should().Be(new VanillaServedFromDump(TalkPath, "Dump.zip", OlderVersion));
    }

    [Fact]
    public async Task AnMsgbndOverlapGoesFromAUnionToAThreeWayMergeOnceTheLibraryHoldsADump()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        var loadout = await TwoMods((MsgPath, Bytes("mod")));
        Sources.Index(loadout).Lookup(MsgPath)!.Strategy.Should().Be(MergeStrategy.FmgUnion);

        await AddDump("Dump", regulation, "vanilla");

        Sources.Index(Refresh(loadout)).Lookup(MsgPath)!.Strategy.Should().Be(MergeStrategy.FmgThreeWay);
    }

    [Fact]
    public async Task OfTwoDumpsTheOneOfTheContributorsRegulationVersionServesTheFile()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var loadout = await TwoMods(("regulation.bin", Regulation(OlderVersion, 1)), (MsgPath, Bytes("mod")));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), "old");
        await AddDump("Current", EldenRingInstall.RegulationFile, "current");
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(MsgPath)!.Contributors;

        var notes = new List<IMergeNote>();
        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply, notes).Should().Equal(Bytes("old"));
        notes.Should().ContainSingle().Which.Should().Be(new VanillaServedFromDump(MsgPath, "Old.zip", OlderVersion));
    }

    [Fact]
    public async Task ChoosingADumpAndVouchingForDumpsReadTheLiveBuildOnceAndAChangedRegulationIsReadAgain()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var loadout = await TwoMods((MsgPath, Bytes("mod")));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), "old");
        await AddDump("Current", EldenRingInstall.RegulationFile, "current");
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(MsgPath)!.Contributors;
        var file = RealGameFolder / "regulation.bin";
        // Indexing already read the build; a new write time starts this Apply from a cold cache.
        File.SetLastWriteTimeUtc(file.GetFullPath(), DateTime.UtcNow.AddMinutes(1));
        _builds.Reads = 0;

        ServiceProvider.GetRequiredService<VouchedRegulations>().Hashes(loadout.InstallationInstance);
        _builds.Reads.Should().Be(1, "vouching reads the installed build through the shared cache");

        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply).Should().Equal(Bytes("current"));
        _builds.Reads.Should().Be(1, "the vouched check and the dump choice share one reading of the installed regulation");

        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply).Should().Equal(Bytes("current"));
        _builds.Reads.Should().Be(1, "the regulation did not change");

        File.WriteAllBytes(file.GetFullPath(), Regulation(NewerVersion, 0xC0));
        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply);
        _builds.Reads.Should().Be(2, "a regulation of another size or write time is a different build");
    }

    [Fact]
    public async Task TheDumpIsChosenByTheSharedReadingOfTheInstalledBuild()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var loadout = await TwoMods((MsgPath, Bytes("mod")));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), "old");
        await AddDump("Current", EldenRingInstall.RegulationFile, "current");
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(MsgPath)!.Contributors;
        // Only a choice made from the shared reading sees this version; the file on disk is the current build.
        _builds.ReportedVersion = OlderVersion;
        File.SetLastWriteTimeUtc((RealGameFolder / "regulation.bin").GetFullPath(), DateTime.UtcNow.AddMinutes(1));

        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply).Should().Equal(Bytes("old"));
    }

    [Fact]
    public async Task ARegulationFromAModOutsideTheOverlapDoesNotPickTheDump()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var loadout = await TwoMods((MsgPath, Bytes("mod")));
        await InstallPackage(loadout.LoadoutId, "Gamma", ("regulation.bin", Regulation(OlderVersion, 1)));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), "old");
        await AddDump("Current", EldenRingInstall.RegulationFile, "current");
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(MsgPath)!.Contributors;

        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply).Should().Equal(Bytes("current"), "neither contributor branched from Gamma's build");
    }

    [Fact]
    public async Task WithoutARegulationFromTheContributorsTheNewestDumpNotNewerThanTheInstallServes()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        KeepBaseline(NewerVersion, Regulation(NewerVersion, 0xB0));
        var loadout = await TwoMods((MsgPath, Bytes("mod")));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), "old");
        await AddDump("Current", EldenRingInstall.RegulationFile, "current");
        await AddDump("Future", Regulation(NewerVersion, 0xB0), "future");
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(MsgPath)!.Contributors;

        Sources.Vanilla(loadout, MsgPath, contributors, MergeSourceMode.Apply).Should().Equal(Bytes("current"));
    }

    [Fact]
    public async Task TheRegulationAncestorComesFromADumpWhenNoBaselineIsKeptAndTheGameHasMovedOn()
    {
        var older = Regulation(OlderVersion, 0xB0);
        _hashDatabase.Add(older.xxHash3());
        var loadout = await TwoMods(("regulation.bin", Regulation(OlderVersion, 1)));
        await AddDump("Dump", older, "vanilla");
        loadout = Refresh(loadout);
        var providers = Sources.Providers(loadout);

        var notes = new List<IMergeNote>();
        Sources.Vanilla(loadout, "regulation.bin", providers, MergeSourceMode.Apply, notes).Should().Equal(older);

        notes.Should().ContainSingle().Which.Should().Be(new VanillaServedFromDump("regulation.bin", "Dump.zip", OlderVersion));
        Directory.Exists((DataRoot / "baselines").GetFullPath()).Should().BeFalse("a dump is read, never kept");
    }

    [Fact]
    public async Task AKeptBaselineOutranksADumpOfTheSameVersion()
    {
        var kept = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, kept);
        var dumped = Regulation(OlderVersion, 0xB1);
        _hashDatabase.Add(dumped.xxHash3());
        var loadout = await TwoMods(("regulation.bin", Regulation(OlderVersion, 1)));
        await AddDump("Dump", dumped, "vanilla");
        loadout = Refresh(loadout);

        Sources.Vanilla(loadout, "regulation.bin", Sources.Providers(loadout), MergeSourceMode.Apply).Should().Equal(kept);
    }

    [Fact]
    public async Task ALoadoutWhoseOnlyLibraryArchivesAreModsOffersNoVanillaPaths()
    {
        var loadout = await TwoMods(("regulation.bin", Regulation(OlderVersion, 1)), (MsgPath, Bytes("mod")));

        Sources.Index(loadout).Lookup(MsgPath)!.Strategy.Should().Be(MergeStrategy.FmgUnion);
        ServiceProvider.GetRequiredService<VanillaDumps>().Paths(Connection.Db, GameInstallation, Sources.Providers(loadout).Select(p => p.RelPath)).Should().BeEmpty();
    }
}
