using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// The merged output of overlapping mods as files in the merged package, with every contributor's copy kept off
/// disk, driven through real applies of an Elden Ring loadout.
/// </summary>
public class MergedFileTests(ITestOutputHelper helper) : AVanillaDumpTest<MergedFileTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string Talk = "script/talk/m00.talkesdbnd.dcx";
    private const string RegulationPath = "regulation.bin";

    private bool _failNextIndex;

    private AbsolutePath Mods => GameInstallation.Locations[LocationId.Me3Mods].Path;

    private AbsolutePath MergedMsg => Mods / ("_merged/" + Msg);

    private ALoadoutSynchronizer AppSynchronizer => (ALoadoutSynchronizer)Synchronizer;

    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        // Fails the first index it is asked for after the test arms it, the way a transient read error would.
        services.AddSingleton<LoadoutMergeSources>(provider => new FailingOnceSources(
            this,
            provider.GetRequiredService<IFileStore>(),
            provider.GetRequiredService<RegulationBaselines>(),
            provider.GetRequiredService<VanillaDumps>(),
            provider.GetRequiredService<ArchiveVanillas>(),
            provider.GetRequiredService<InstalledBuilds>(),
            provider.GetRequiredService<ILogger<LoadoutMergeSources>>()));
        return services;
    }

    private sealed class FailingOnceSources(MergedFileTests test, IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, ArchiveVanillas archives, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
        : LoadoutMergeSources(fileStore, baselines, dumps, archives, builds, logger)
    {
        public override MergePathIndex Index(Loadout.ReadOnly loadout)
        {
            if (!test._failNextIndex) return base.Index(loadout);

            test._failNextIndex = false;
            throw new IOException("the library could not be read");
        }
    }

    private static byte[] Msgbnd(string sharedText, int ownId, string ownText)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = sharedText, [ownId] = ownText },
        }, Formats);

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string SharedText(byte[] archive) => MsgbndFixture.ReadBack(archive, Formats)[1][10]!;

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
        return result.LoadoutItemGroup!.Value;
    }

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private string[] ProfilePackages()
        => File.ReadAllLines((Mods / "silver.me3").GetFullPath())
            .Where(line => line.StartsWith("id = ", StringComparison.Ordinal))
            .Select(line => line["id = ".Length..].Trim('"'))
            .ToArray();

    // Alpha is installed first, so Beta starts out with the higher priority.
    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Alpha, LoadoutItemGroup.ReadOnly Beta, byte[] AlphaMsg, byte[] BetaMsg)> TwoMsgbndMods(bool bothShipAPart)
    {
        var loadout = await CreateLoadout();
        var alphaMsg = Msgbnd("alpha text", 11, "alpha only");
        var betaMsg = Msgbnd("beta text", 12, "beta only");
        var alpha = await InstallMod(loadout.LoadoutId, "Alpha", (Msg, alphaMsg), ("parts/alpha.dcx", Bytes("alpha part")));
        var beta = bothShipAPart
            ? await InstallMod(loadout.LoadoutId, "Beta", (Msg, betaMsg), ("parts/beta.dcx", Bytes("beta part")))
            : await InstallMod(loadout.LoadoutId, "Beta", (Msg, betaMsg));
        return (Refresh(loadout), alpha, beta, alphaMsg, betaMsg);
    }

    [Fact]
    public async Task TheMergedFileIsWrittenWithThePlannersBytesAndNoContributorCopyIsOnDisk()
    {
        var (loadout, _, _, alphaMsg, betaMsg) = await TwoMsgbndMods(bothShipAPart: false);

        await Apply(loadout);

        var merged = await MergedMsg.ReadAllBytesAsync();
        merged.Should().Equal(FmgMerge.Union(betaMsg, alphaMsg, Formats));
        SharedText(merged).Should().Be("beta text", "the mod on top wins the id both edited");
        MsgbndFixture.ReadBack(merged, Formats)[1].Keys.Should().BeEquivalentTo([10, 11, 12]);
        (Mods / "Alpha" / Msg).FileExists.Should().BeFalse();
        (Mods / "Beta" / Msg).FileExists.Should().BeFalse();
        Directory.GetFiles(Mods.GetFullPath(), "item.msgbnd.dcx", SearchOption.AllDirectories).Should().Equal(MergedMsg.GetFullPath());
    }

    [Fact]
    public async Task TheProfileListsThePackagesThatKeepAFileInPriorityOrderWithTheMergedPackageLast()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: false);

        await Apply(loadout);

        // Beta's only file was merged away, so me3 has no folder to mount for it.
        ProfilePackages().Should().Equal("Alpha", "_merged");
    }

    [Fact]
    public async Task FlippingThePriorityFlipsTheMergedTextAndTheProfileOrder()
    {
        var (loadout, alpha, beta, _, _) = await TwoMsgbndMods(bothShipAPart: true);
        await Apply(loadout);
        SharedText(await MergedMsg.ReadAllBytesAsync()).Should().Be("beta text");
        ProfilePackages().Should().Equal("Alpha", "Beta", "_merged");

        var alphaRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, alpha.LoadoutItemGroupId).Single();
        var betaRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, beta.LoadoutItemGroupId).Single();
        await LoadoutManager.ResolveFileConflicts([alphaRow.LoadoutItemGroupPriorityId], betaRow.LoadoutItemGroupPriorityId);
        await Apply(loadout);

        SharedText(await MergedMsg.ReadAllBytesAsync()).Should().Be("alpha text");
        ProfilePackages().Should().Equal("Beta", "Alpha", "_merged");
    }

    [Fact]
    public async Task AHandEditedMergedFileIsReplacedOnTheNextApply()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: false);
        await Apply(loadout);
        var generated = await MergedMsg.ReadAllBytesAsync();

        await File.WriteAllBytesAsync(MergedMsg.GetFullPath(), Bytes("edited by hand"));
        await Apply(loadout);

        (await MergedMsg.ReadAllBytesAsync()).Should().Equal(generated);
    }

    [Fact]
    public async Task AHandEditedContributorCopyIsBackedUpAndRemovedAndTheLoadoutKeepsTheModsFile()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: false);
        await Apply(loadout);
        var before = Sources.Providers(Refresh(loadout)).ToDictionary(p => $"{p.PackageId}/{p.RelPath}", p => p.Hash);

        var edited = Bytes("a copy someone dropped back in");
        var copy = Mods / "Alpha" / Msg;
        copy.Parent.CreateDirectory();
        await File.WriteAllBytesAsync(copy.GetFullPath(), edited);
        await Apply(loadout);

        copy.FileExists.Should().BeFalse();
        (await FileStore.HaveFile(edited.xxHash3())).Should().BeTrue("the edit is kept before it is removed");
        Sources.Providers(Refresh(loadout)).ToDictionary(p => $"{p.PackageId}/{p.RelPath}", p => p.Hash).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task TwoAppliesProduceIdenticalOutput()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: true);

        await Apply(loadout);
        var merged = await MergedMsg.ReadAllBytesAsync();
        var profile = await (Mods / "silver.me3").ReadAllBytesAsync();
        await Apply(loadout);

        (await MergedMsg.ReadAllBytesAsync()).Should().Equal(merged);
        (await (Mods / "silver.me3").ReadAllBytesAsync()).Should().Equal(profile);
    }

    [Fact]
    public async Task AtSteadyStateTheContributorNodesNeedNothingAndTheGeneratedFilesAreRewritten()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: true);
        await Apply(loadout);
        await Apply(loadout);

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        tree[new GamePath(LocationId.Me3Mods, "Alpha/" + Msg)].Actions.Should().Be(Actions.DoNothing);
        tree[new GamePath(LocationId.Me3Mods, "Beta/" + Msg)].Actions.Should().Be(Actions.DoNothing);
        tree[new GamePath(LocationId.Me3Mods, "_merged/" + Msg)].Actions.Should().Be(Actions.WriteIntrinsic);
        tree[new GamePath(LocationId.Me3Mods, "silver.me3")].Actions.Should().Be(Actions.WriteIntrinsic);
        Sources.Providers(Refresh(loadout)).Where(p => p.RelPath == Msg).Select(p => p.PackageId).Should().BeEquivalentTo(["Alpha", "Beta"]);
    }

    [Fact]
    public async Task DisablingAContributorRestoresTheRemainingCopyAndReEnablingItMergesAgain()
    {
        var (loadout, alpha, _, alphaMsg, betaMsg) = await TwoMsgbndMods(bothShipAPart: true);
        await Apply(loadout);

        await DisableItem(alpha.Id);
        await Apply(loadout);

        MergedMsg.FileExists.Should().BeFalse();
        (await (Mods / "Beta" / Msg).ReadAllBytesAsync()).Should().Equal(betaMsg);
        (Mods / "Alpha" / Msg).FileExists.Should().BeFalse();

        await EnableItem(alpha.Id);
        await Apply(loadout);

        (await MergedMsg.ReadAllBytesAsync()).Should().Equal(FmgMerge.Union(betaMsg, alphaMsg, Formats));
        (Mods / "Beta" / Msg).FileExists.Should().BeFalse();
        (Mods / "Alpha" / Msg).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task APairTheEngineCannotMergeDeploysBothCopiesAndWritesNoMergedFile()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", (Talk, Bytes("alpha talk")));
        await InstallMod(loadout.LoadoutId, "Beta", (Talk, Bytes("beta talk")));

        await Apply(loadout);

        (await (Mods / "Alpha" / Talk).ReadAllBytesAsync()).Should().Equal(Bytes("alpha talk"));
        (await (Mods / "Beta" / Talk).ReadAllBytesAsync()).Should().Equal(Bytes("beta talk"));
        (Mods / "_merged").DirectoryExists().Should().BeFalse();
        ProfilePackages().Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task AMergeThatFailsDeploysBothCopiesSoMe3MountsTheLoadoutPriorityWinner()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", (Msg, Bytes("not an archive, alpha")), ("parts/alpha.dcx", Bytes("alpha part")));
        await InstallMod(loadout.LoadoutId, "Beta", (Msg, Bytes("not an archive, beta")));

        await Apply(Refresh(loadout));

        (await (Mods / "Alpha" / Msg).ReadAllBytesAsync()).Should().Equal(Bytes("not an archive, alpha"));
        (await (Mods / "Beta" / Msg).ReadAllBytesAsync()).Should().Equal(Bytes("not an archive, beta"));
        (Mods / "_merged").DirectoryExists().Should().BeFalse();
        // Beta is installed last, so it has the higher priority and me3 mounts the package the profile lists last.
        ProfilePackages().Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task AMergeThatFailsAfterItWasMergedTakesTheOldMergedFileOffDisk()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: false);
        await Apply(loadout);
        MergedMsg.FileExists.Should().BeTrue();
        await InstallMod(loadout.LoadoutId, "Gamma", (Msg, Bytes("not an archive")));

        await Apply(Refresh(loadout));

        MergedMsg.FileExists.Should().BeFalse("a stale merged file would be mounted over the winner's copy");
        (Mods / "Gamma" / Msg).FileExists.Should().BeTrue();
        (Mods / "Alpha" / Msg).FileExists.Should().BeTrue();
    }

    private async Task<Loadout.ReadOnly> TwoRegulationsOfDifferentVersions(string alphaFile)
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(GameVersion, 0x10)), ("parts/alpha.dcx", Bytes(alphaFile)));
        return Refresh(loadout);
    }

    [Fact]
    public async Task ARefusedMergeRefusesTheApplyAndLeavesTheDiskAndTheLoadoutAlone()
    {
        var loadout = await TwoRegulationsOfDifferentVersions("alpha part");
        await Apply(loadout);
        await InstallMod(loadout.LoadoutId, "Beta", (RegulationPath, Regulation(OlderVersion, 0x20)));
        var disk = TreeSnapshot.Take(Mods);
        var tx = Connection.Db.BasisTxId;

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var thrown = await act.Should().ThrowAsync<MergeRefusedException>();
        thrown.Which.Message.Should().Contain(RegulationPath).And.Contain($"no vanilla regulation of version {OlderVersion}").And.Contain("Beta");
        thrown.Which.Should().BeAssignableTo<SynchronizerPreflightException>();
        TreeSnapshot.Take(Mods).Should().Equal(disk);
        Connection.Db.BasisTxId.Should().Be(tx, "nothing was written to the loadout either");
    }

    [Fact]
    public async Task ARefusedTargetOnASwitchLeavesThePreviousLoadoutDeployed()
    {
        var first = await TwoRegulationsOfDifferentVersions("alpha part");
        await Apply(first);
        var second = await CreateLoadout();
        await InstallMod(second.LoadoutId, "Gamma", (RegulationPath, Regulation(GameVersion, 0x30)));
        await InstallMod(second.LoadoutId, "Delta", (RegulationPath, Regulation(OlderVersion, 0x40)));

        var act = async () => await Synchronizer.Synchronize(Refresh(second));

        await act.Should().ThrowAsync<MergeRefusedException>();
        (Mods / "Alpha" / "parts/alpha.dcx").FileExists.Should().BeTrue("the first loadout was not deactivated");
        GameInstallMetadata.LastSyncedLoadout.TryGetValue(Refresh(first).Installation, out var lastSynced).Should().BeTrue();
        lastSynced.Should().Be(first.Id);
    }

    [Fact]
    public async Task AFailureWhileListingTheFilesIsSurvivedByTheListingAndRefusesTheNextApply()
    {
        var (loadout, _, _, _, _) = await TwoMsgbndMods(bothShipAPart: false);
        await Apply(loadout);
        // A new revision, so the listing is asked for again instead of answered from what it remembered.
        await InstallMod(loadout.LoadoutId, "Gamma", ("parts/gamma.dcx", Bytes("gamma part")));
        var latest = Refresh(loadout);
        var disk = TreeSnapshot.Take(Mods);

        _failNextIndex = true;
        var files = AppSynchronizer.IntrinsicFiles(latest);

        files.Should().ContainKey(new GamePath(LocationId.Me3Mods, "silver.me3"), "the loader's files survive");
        files.Keys.Should().NotContain(path => path.Path.Path.StartsWith("_merged/", StringComparison.Ordinal));

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var thrown = await act.Should().ThrowAsync<MergeRefusedException>();
        thrown.Which.Message.Should().Contain("could not be read");
        TreeSnapshot.Take(Mods).Should().Equal(disk);
    }
}
