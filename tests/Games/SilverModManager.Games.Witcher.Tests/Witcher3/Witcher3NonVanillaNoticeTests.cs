using System.Collections.Frozen;
using System.Text;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

/// <summary>
/// The "Game files differ from the store's build" card on a Witcher 3 whose loadout has been applied and whose
/// baseline was rebuilt afterwards, so the files the loadout deployed are listed as non-vanilla rows in it.
/// </summary>
public class Witcher3NonVanillaNoticeTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<Witcher3NonVanillaNoticeTests, Witcher3Game>(helper)
{
    private const uint AppId = 292030;
    private const uint DepotId = 292031;
    private const ulong ManifestId = 9000000000000000001;

    private static readonly RelativePath[] ModFiles =
    [
        "Mods/modPOTT/content/scripts/game/pott.ws",
        "Mods/modbrothersinarms/content/scripts/game/arms.ws",
        "dlc/dlcArdDhuStable/content/ard.bundle",
    ];

    private const string MergedScript = "Mods/mod0000_MergedFiles/content/scripts/game/player.ws";

    private static string Script(int line = 0, string text = "") =>
        string.Join("\n", Enumerable.Range(1, 10).Select(i => i == line ? text : $"  line{i}();")) + "\n";

    protected override bool StoreVouchesForTheFiles => true;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddWitcherGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var depot = new SteamDepot(DepotId, ManifestId,
        [
            new SteamFile("bin/x64/witcher3.exe", "executable"),
            new SteamFile("content/metadata.store", "vanilla metadata store"),
            new SteamFile("content/blob0.bundle", "bundle"),
            new SteamFile("content/content0/scripts/game/player.ws", Script()),
        ]);
        return library.AddGame(AppId, "The Witcher 3", "The Witcher 3: Wild Hunt", 26238013, depot);
    }

    /// <summary>A mod folder that changes the player script, with its bytes in the file store as an installed mod's are.</summary>
    private async Task<LoadoutItemGroupId> ScriptMod(Loadout.ReadOnly loadout, string folder, ulong priority, string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script);
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        var path = new GamePath(LocationId.Game, $"Mods/{folder}/content/scripts/game/player.ws");
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, folder);
        _ = new LoadoutFile.New(tx, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, ParentId = group, Name = path.Path },
                TargetPath = path.ToGamePathParentTuple(loadout.LoadoutId),
            },
            Hash = hash,
            Size = size,
        };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), hash, size)]);
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private LoadoutItemGroupId _modB;

    private async Task<Loadout.ReadOnly> AppliedLoadoutWithBaselineRebuiltAfterwards()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ModFiles, loadout.LoadoutId, "Deployed mods");
            await tx.Commit();
        }

        _modB = default;
        await ScriptMod(loadout, "modA", 1, Script(2, "  a();"));
        _modB = await ScriptMod(loadout, "modB", 2, Script(8, "  b();"));
        await Synchronizer.Synchronize(Refresh(loadout));

        await (GameFolder / "readme-from-the-user.txt").WriteAllBytesAsync("mine"u8.ToArray());

        // The live-update pass rebuilds the baseline from the folder as it is now, deployed files included.
        var hashes = ServiceProvider.GetRequiredService<IFileHashesService>();
        await hashes.RemoveLocalBaseline(GameInstallation.LocatorResult.Store, GameFolder.ToString());
        var built = await hashes.EnsureLocalBaseline(GameInstallation);
        var rows = SilverModManager.Abstractions.Games.FileHashes.Models.NonVanillaFile.All(hashes.Local).Select(r => r.Path.ToString()).ToArray();
        (GameFolder / MergedScript).FileExists.Should().BeTrue("two mods change the same script, so the apply wrote the merged copy");
        rows.Should().Contain(MergedScript);
        rows.Should().Contain("Mods/modPOTT/content/scripts/game/pott.ws", $"the rebuilt baseline ({built}) lists the deployed files as non-vanilla");
        // The player applies again with the rebuilt baseline in place.
        await Synchronizer.Synchronize(Refresh(loadout));
        OverridePaths(Refresh(loadout)).Should().Equal(["readme-from-the-user.txt"], "only the player's own file is filed as an override");
        return Refresh(loadout);
    }

    private string[] OverridePaths(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString()).ToArray();

    private async Task<string[]> NonVanillaCards(Loadout.ReadOnly loadout)
    {
        // The diagnostic manager's route: the recorded disk states, not the index.
        var metadata = loadout.Installation;
        var scanned = Synchronizer.GetLastScannedDiskState(metadata);
        var previous = Synchronizer.GetPreviouslyAppliedDiskState(metadata);
        var tree = Synchronizer.BuildSyncTree(scanned, previous, loadout);
        Synchronizer.ProcessSyncTree(tree);
        var frozen = tree.ToFrozenDictionary();
        var emitters = GameInstallation.GetGame().DiagnosticEmitters
            .Concat(ServiceProvider.GetServices<IDiagnosticEmitter>())
            .OfType<ILoadoutDiagnosticEmitter>();

        var cards = new List<string>();
        foreach (var emitter in emitters)
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, frozen, CancellationToken.None))
            {
                var text = Describe(diagnostic);
                if (text.Contains("already there before", StringComparison.Ordinal)) cards.Add(text);
            }
        }

        return cards.ToArray();
    }

    [Fact]
    public async Task TheFilesTheLoadoutDeploysAreNotListedAsAlreadyThereBeforeTheAppManagedTheGame()
    {
        var loadout = await AppliedLoadoutWithBaselineRebuiltAfterwards();

        var cards = await NonVanillaCards(loadout);

        cards.Should().ContainSingle(card => card.Contains("readme-from-the-user.txt"), "a file nobody here put there is still reported");
        cards.Should().NotContain(card => card.Contains("mod0000_MergedFiles"), "the merged copy is this app's own output");
        cards.Should().NotContain(card => ModFiles.Any(file => card.Contains(file.Path, StringComparison.OrdinalIgnoreCase)),
            "the loadout put these files there");
    }

    [Fact]
    public async Task TheMergedCopyAStaleMergeLeavesOnDisk_IsNotListedAsAlreadyThereBeforeTheAppManagedTheGame()
    {
        var loadout = await AppliedLoadoutWithBaselineRebuiltAfterwards();

        // modB goes off, so nothing is contested any more; the merged copy stays on disk until the next apply removes it.
        await DisableItem(_modB.Value);

        var cards = await NonVanillaCards(Refresh(loadout));

        (GameFolder / MergedScript).FileExists.Should().BeTrue("the next apply has not run");
        cards.Should().ContainSingle(card => card.Contains("readme-from-the-user.txt"));
        cards.Should().NotContain(card => card.Contains("mod0000_MergedFiles") || card.Contains("modB"), "this app wrote them");
    }
}
