using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Some mods are nothing but save files. A mod flagged with <see cref="LoadoutItem.ProtectedPathsAllowed"/>
/// deploys into the save folder, but only ever touches files it put there itself.
/// </summary>
public class ProtectedPathsOverrideTests(ITestOutputHelper helper) : AStubbedGameTest<ProtectedPathsOverrideTests>(helper)
{
    private static readonly byte[] UserSaveBytes = "the player's own save"u8.ToArray();
    private static readonly byte[] ModSaveBytes = "a save shipped by a mod"u8.ToArray();
    private static readonly byte[] ReplacementBytes = "mod save that must not replace the player's"u8.ToArray();

    private static readonly GamePath UserSavePath = new(LocationId.Saves, "saves/save1.dat");
    private static readonly GamePath ModSavePath = new(LocationId.Saves, "saves/mod.sav");

    private AbsolutePath UserSave => GameInstallation.Locations.ToAbsolutePath(UserSavePath);
    private AbsolutePath ModSave => GameInstallation.Locations.ToAbsolutePath(ModSavePath);
    private AbsolutePath ModGameFile => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/ok.txt"));

    protected override async Task GenerateGameFiles()
    {
        UserSave.Parent.CreateDirectory();
        await UserSave.WriteAllBytesAsync(UserSaveBytes);
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Mod)> InstallMod(Loadout.ReadOnly loadout, Dictionary<RelativePath, byte[]> files, bool allowProtectedPaths)
    {
        await using var archiveFile = await CreateTestArchive(files);
        var archive = await RegisterLocalArchive(archiveFile.Path);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        var mod = result.LoadoutItemGroup!.Value;

        if (allowProtectedPaths)
        {
            using var tx = Connection.BeginTransaction();
            tx.Add(mod.Id, LoadoutItem.ProtectedPathsAllowed, Null.Instance);
            await tx.Commit();
        }

        return (Refresh(loadout), mod);
    }

    private static Dictionary<RelativePath, byte[]> SaveModFiles() => new()
    {
        { "saves/mod.sav", ModSaveBytes },
        { "game/ok.txt", "a normal mod file"u8.ToArray() },
    };

    private IReadOnlyList<GamePath> IndexedPaths()
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return Synchronizer.GetDiskStateForGame(metadata).Select(p => p.Path).ToList();
    }

    [Fact]
    public async Task WithoutTheFlag_ModSaveFilesAreSkipped()
    {
        var (loadout, _) = await InstallMod(await CreateLoadout(), SaveModFiles(), allowProtectedPaths: false);
        await Synchronizer.Synchronize(loadout);

        ModSave.FileExists.Should().BeFalse();
        ModGameFile.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task WithTheFlag_ModSaveFilesDeploy()
    {
        var (loadout, _) = await InstallMod(await CreateLoadout(), SaveModFiles(), allowProtectedPaths: true);
        loadout = await Synchronizer.Synchronize(loadout);

        ModSave.FileExists.Should().BeTrue();
        (await ModSave.ReadAllBytesAsync()).Should().Equal(ModSaveBytes);
        ModGameFile.FileExists.Should().BeTrue();

        // The deployed file has to stay tracked, or the next sync would take it for the user's.
        IndexedPaths().Should().Contain(ModSavePath);
        IndexedPaths().Should().NotContain(UserSavePath, "the player's own save is still never indexed");

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        ModSave.FileExists.Should().BeTrue("a second sync keeps what the first deployed");
        (await ModSave.ReadAllBytesAsync()).Should().Equal(ModSaveBytes);
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
    }

    [Fact]
    public async Task WithTheFlag_SharedProtonCachesAreStillSkipped()
    {
        var cachePath = new GamePath(LocationId.Game, "game/mod.dxvk-cache");
        var files = new Dictionary<RelativePath, byte[]>
        {
            { "game/mod.dxvk-cache", "a cache shipped by a mod"u8.ToArray() },
            { "saves/mod.sav", ModSaveBytes },
        };
        var (loadout, _) = await InstallMod(await CreateLoadout(), files, allowProtectedPaths: true);
        await Synchronizer.Synchronize(loadout);

        // A cache is never indexed, so once written nothing could take it off disk again.
        GameInstallation.Locations.ToAbsolutePath(cachePath).FileExists.Should().BeFalse();
        ModSave.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task WithTheFlag_ExistingUserSaveIsNeverOverwritten()
    {
        var files = new Dictionary<RelativePath, byte[]>
        {
            { "saves/save1.dat", ReplacementBytes },
            { "saves/mod.sav", ModSaveBytes },
        };
        var (loadout, _) = await InstallMod(await CreateLoadout(), files, allowProtectedPaths: true);

        loadout = await Synchronizer.Synchronize(loadout);

        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
        (await ModSave.ReadAllBytesAsync()).Should().Equal(ModSaveBytes, "the mod's other files still deploy");
        IndexedPaths().Should().NotContain(UserSavePath);

        // Neither a repeat sync nor a loadout switch may change the verdict.
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
        _ = await Synchronizer.Synchronize(await CreateLoadout());
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
    }

    [Fact]
    public async Task UnManage_RemovesOnlyTheDeployedSaves()
    {
        var files = new Dictionary<RelativePath, byte[]>
        {
            { "saves/save1.dat", ReplacementBytes },
            { "saves/mod.sav", ModSaveBytes },
        };
        var (loadout, _) = await InstallMod(await CreateLoadout(), files, allowProtectedPaths: true);
        await Synchronizer.Synchronize(loadout);
        ModSave.FileExists.Should().BeTrue();

        await LoadoutManager.UnManage(GameInstallation);

        ModSave.FileExists.Should().BeFalse("the loadout deployed it");
        UserSave.FileExists.Should().BeTrue();
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
    }

    [Fact]
    public async Task ResetToOriginalGameState_RemovesOnlyTheDeployedSaves()
    {
        var (loadout, _) = await InstallMod(await CreateLoadout(), SaveModFiles(), allowProtectedPaths: true);
        await Synchronizer.Synchronize(loadout);
        ModSave.FileExists.Should().BeTrue();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);

        ModSave.FileExists.Should().BeFalse("the loadout deployed it");
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
    }

    [Fact]
    public async Task SwitchingAwayFromTheLoadout_RemovesTheDeployedSaves()
    {
        var (loadout, _) = await InstallMod(await CreateLoadout(), SaveModFiles(), allowProtectedPaths: true);
        await Synchronizer.Synchronize(loadout);
        ModSave.FileExists.Should().BeTrue();

        _ = await Synchronizer.Synchronize(await CreateLoadout());

        ModSave.FileExists.Should().BeFalse();
        (await UserSave.ReadAllBytesAsync()).Should().Equal(UserSaveBytes);
    }

    [Fact]
    public async Task DeployedSaveRewrittenByTheGame_IsNeverOverwrittenOrResetAway()
    {
        var (loadout, _) = await InstallMod(await CreateLoadout(), SaveModFiles(), allowProtectedPaths: true);
        loadout = await Synchronizer.Synchronize(loadout);

        (await ModSave.ReadAllBytesAsync()).Should().Equal(ModSaveBytes);

        var played = "the mod's save after the player carried on from it"u8.ToArray();
        await ModSave.WriteAllBytesAsync(played);

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        (await ModSave.ReadAllBytesAsync()).Should().Equal(played);

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        (await ModSave.ReadAllBytesAsync()).Should().Equal(played, "progress made since the deploy is the player's");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);
        (await ModSave.ReadAllBytesAsync()).Should().Equal(played);
    }
}
