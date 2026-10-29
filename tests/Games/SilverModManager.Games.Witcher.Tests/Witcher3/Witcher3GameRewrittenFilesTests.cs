using System.Collections.Frozen;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

/// <summary>
/// The next-gen Witcher 3 rebuilds <c>content/metadata.store</c> and its stamp every time the set of mod and DLC
/// bundles changes, and its script compiler writes <c>x64.final.redscripts</c>. Steam lists the store as a vanilla
/// file, so the bytes the game leaves behind must not read as a changed game file.
/// </summary>
public class Witcher3GameRewrittenFilesTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<Witcher3GameRewrittenFilesTests, Witcher3Game>(helper)
{
    private const uint AppId = 292030;
    private const uint DepotId = 292031;
    private const ulong ManifestId = 9000000000000000001;

    private const string MetadataStore = "content/metadata.store";
    private const string Stamp = "content/metadata.store.stamp";
    private const string CompiledScripts = "content/content0/x64.final.redscripts";

    private static readonly string[] GameRewritten = [MetadataStore, Stamp, CompiledScripts];

    protected override bool StoreVouchesForTheFiles => true;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddWitcherGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var depot = new SteamDepot(DepotId, ManifestId,
        [
            new SteamFile("bin/x64/witcher3.exe", "executable"),
            new SteamFile("bin/x64_DX12/witcher3.exe", "dx12 executable"),
            new SteamFile(MetadataStore, "vanilla metadata store"),
            new SteamFile("content/content0/cookedfinal.redscripts", "steam listed scripts"),
            new SteamFile("content/blob0.bundle", "bundle"),
        ]);
        return library.AddGame(AppId, "The Witcher 3", "The Witcher 3: Wild Hunt", 26238013, depot);
    }

    /// <summary>What the game leaves behind after it starts: a rebuilt store, its stamp and a compiled script cache.</summary>
    private async Task TheGameRunsOnce()
    {
        foreach (var path in GameRewritten)
        {
            var file = GameFolder / path;
            file.Parent.CreateDirectory();
            await file.WriteAllBytesAsync(Encoding.UTF8.GetBytes("written by the game: " + path));
        }
    }

    private static string[] Paths(IEnumerable<GamePath> paths) => paths.Select(p => p.Path.ToString()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Like the diagnostic manager: the tree is processed before the emitters read it, which is what sets the warnings.</summary>
    private async Task<Diagnostic[]> RunEmittersOnProcessedTree(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);
        var frozen = tree.ToFrozenDictionary();
        var emitters = GameInstallation.GetGame().DiagnosticEmitters
            .Concat(ServiceProvider.GetServices<IDiagnosticEmitter>())
            .OfType<ILoadoutDiagnosticEmitter>();

        var found = new List<Diagnostic>();
        foreach (var emitter in emitters)
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, frozen, CancellationToken.None))
                found.Add(diagnostic);
        }

        return found.ToArray();
    }

    [Fact]
    public async Task TheStoreTheGameRewroteIsNotAChangedGameFile()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Synchronizer.Synchronize(Refresh(loadout));

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        tree.Where(kv => kv.Value.Actions.HasFlag(Actions.WarnOfUnableToExtract)).Select(kv => kv.Key).Should().BeEmpty();
        Paths(tree.Keys).Should().NotContain(GameRewritten, "the game's own files are not part of the sync");
    }

    [Fact]
    public async Task TheFilesTheGameRewroteRaiseNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Synchronizer.Synchronize(Refresh(loadout));

        var diagnostics = await RunEmittersOnProcessedTree(Refresh(loadout));

        diagnostics.Select(Describe).Where(text => GameRewritten.Any(text.Contains)).Should().BeEmpty();
        diagnostics.Select(Describe).Should().NotContain(text => text.Contains("Loadout can't be applied"), "no data mismatch card blocks the apply");
    }

    [Fact]
    public async Task AnApplyAfterTheGameRanGoesAheadAndLeavesTheGamesFilesAsTheyAre()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();

        await Synchronizer.Synchronize(Refresh(loadout));

        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path);
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString());
        overrides.Should().NotContain(GameRewritten, "the game's own output is not the player's mod");
    }

    [Fact]
    public async Task ADeletedMetadataStoreIsNotRestoredByAnApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / MetadataStore).Delete();
        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / MetadataStore).FileExists.Should().BeFalse("the game rebuilds it itself when it starts");
    }
}
