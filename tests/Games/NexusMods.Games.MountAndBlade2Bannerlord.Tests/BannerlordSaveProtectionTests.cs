using FluentAssertions;
using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.MountAndBlade2Bannerlord.Tests;

/// <summary>
/// A Bannerlord install that resolves its Documents folder inside a Wine prefix (GOG or Epic under
/// Wine) has a save folder the synchronizer must leave alone: the game's own rules do not filter it.
/// </summary>
public class BannerlordSaveProtectionTests(ITestOutputHelper helper) : AIsolatedGameTest<BannerlordSaveProtectionTests, Bannerlord>(helper)
{
    private static readonly byte[] SaveBytes = "bannerlord save, never ingest me"u8.ToArray();

    private AbsolutePath SaveFile => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Saves, "slot1.sav"));

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddMountAndBlade2Bannerlord()
            .AddUniversalGameLocator<Bannerlord>(new Version("1.0"), locationsFileSystem: provider =>
            {
                var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
                var prefix = new WinePrefix { ConfigurationDirectory = pfx.Path };
                var overlay = prefix.CreateOverlayFileSystem(NexusMods.Paths.FileSystem.Shared);
                overlay.GetKnownPath(KnownPath.MyDocumentsDirectory).CreateDirectory();
                return overlay;
            });
    }

    protected override async Task GenerateGameFiles()
    {
        SaveFile.Parent.CreateDirectory();
        await SaveFile.WriteAllBytesAsync(SaveBytes);
    }

    [Fact]
    public async Task SavesInThePrefix_SurviveEverySynchronizerOperation()
    {
        SaveFile.GetFullPath().Should().Contain("/drive_c/users/", "the Saves location resolves inside the prefix");

        var loadoutA = await CreateLoadout();
        Synchronizer.GetDiskStateForGame(GameRegistry.ForceGetMetadata(GameInstallation))
            .Should().NotContain(p => p.Path.LocationId == LocationId.Saves);
        (await FileStore.HaveFile(SaveBytes.xxHash3())).Should().BeFalse();
        LoadoutItem.FindByLoadout(loadoutA.Db, loadoutA).OfTypeLoadoutItemWithTargetPath()
            .Select(i => ((GamePath)i.TargetPath).LocationId).Should().NotContain(LocationId.Saves);

        var loadoutB = await CreateLoadout();
        _ = await Synchronizer.Synchronize(loadoutB);
        await AssertSaveIntact();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);
        await AssertSaveIntact();

        await LoadoutManager.DeleteLoadout(loadoutA.LoadoutId);
        await AssertSaveIntact();

        await LoadoutManager.UnManage(GameInstallation);
        await AssertSaveIntact();
    }

    private async Task AssertSaveIntact()
    {
        SaveFile.FileExists.Should().BeTrue();
        (await SaveFile.ReadAllBytesAsync()).Should().Equal(SaveBytes);
    }
}
