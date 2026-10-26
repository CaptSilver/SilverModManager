using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Activating a loadout must keep a file in Preferences that neither loadout carries. Such a file (a game's own
/// INI, written after the loadouts were made) is the user's, and the incoming loadout adopts it. A loadout item
/// at the same path still wins, with the user's copy backed up, and a delete in the incoming loadout still
/// removes it.
/// </summary>
/// <remarks>
/// Not covered by the loadout-swap test in <c>GeneralLoadoutManagementTests</c>: its Preferences file belongs to
/// a loadout, so it survives as a loadout item and never exercises a file that is in no loadout at all.
/// </remarks>
public class PreferencesUserFilesSwitchTests(ITestOutputHelper helper) : AStubbedGameTest<PreferencesUserFilesSwitchTests>(helper)
{
    private static readonly byte[] UserIniBytes = "[Display]\nbFull Screen=0\n"u8.ToArray();
    private static readonly byte[] ModIniBytes = "[Display]\nbFull Screen=1\n"u8.ToArray();

    private AbsolutePath UserIni => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "SkyrimPrefs.ini"));

    private async Task WriteUserIni()
    {
        UserIni.Parent.CreateDirectory();
        await UserIni.WriteAllBytesAsync(UserIniBytes);
    }

    [Fact]
    public async Task AFileInNoLoadoutSurvivesSwitchingAwayAndBack()
    {
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);

        await WriteUserIni();

        await LoadoutManager.ActivateLoadout(loadoutB.LoadoutId);
        UserIni.FileExists.Should().BeTrue("the file is in neither loadout, so activating B must not delete it");
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);

        await LoadoutManager.ActivateLoadout(loadoutA.LoadoutId);
        UserIni.FileExists.Should().BeTrue();
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);
    }

    [Fact]
    public async Task AFileInNoLoadoutSurvivesTheFirstApplyAfterTheSwitch()
    {
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);

        await WriteUserIni();

        await LoadoutManager.ActivateLoadout(loadoutB.LoadoutId);
        await Synchronizer.Synchronize(Refresh(loadoutB));

        UserIni.FileExists.Should().BeTrue("the apply after the switch must not read the kept file as one B removed");
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);
    }

    [Fact]
    public async Task AFileInNoLoadoutSurvivesASwitchThroughSynchronize()
    {
        // The route the app takes: Synchronize applies A to pick up changes, resets the game, then activates B.
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);

        await WriteUserIni();

        await Synchronizer.Synchronize(Refresh(loadoutB));
        UserIni.FileExists.Should().BeTrue();
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);

        await Synchronizer.Synchronize(Refresh(loadoutB));
        UserIni.FileExists.Should().BeTrue();
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);

        await Synchronizer.Synchronize(Refresh(loadoutA));
        UserIni.FileExists.Should().BeTrue();
        (await UserIni.ReadAllBytesAsync()).Should().Equal(UserIniBytes);
    }

    [Fact]
    public async Task AFileTheIncomingLoadoutCarriesOverwritesTheUsersCopyWithABackup()
    {
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);

        await using var archiveFile = await CreateTestArchive(new Dictionary<RelativePath, byte[]>
        {
            { "preferences/SkyrimPrefs.ini", ModIniBytes },
        });
        var archive = await RegisterLocalArchive(archiveFile.Path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadoutB, installer: new PreferencesInstaller(ServiceProvider));

        await WriteUserIni();

        await LoadoutManager.ActivateLoadout(loadoutB.LoadoutId);

        (await UserIni.ReadAllBytesAsync()).Should().Equal(ModIniBytes);
        (await FileStore.HaveFile(UserIniBytes.xxHash3())).Should().BeTrue("the user's copy is backed up before it is overwritten");
    }

    [Fact]
    public async Task AFileTheIncomingLoadoutDeletesIsStillRemoved()
    {
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadoutB.LoadoutId, "Deleter");
            var path = new GamePath(LocationId.Preferences, "SkyrimPrefs.ini");
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Reified delete",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = path.ToGamePathParentTuple(loadoutB.LoadoutId),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        LoadoutId = loadoutB.LoadoutId,
                        Name = path.Path,
                        ParentId = group,
                    },
                },
            };
            await tx.Commit();
        }

        await WriteUserIni();

        await LoadoutManager.ActivateLoadout(loadoutB.LoadoutId);

        UserIni.FileExists.Should().BeFalse("B says the file is gone");
    }

    private class PreferencesInstaller(IServiceProvider serviceProvider)
        : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<PreferencesInstaller>>())
    {
        public override ValueTask<InstallerResult> ExecuteAsync(
            LibraryArchive.ReadOnly libraryArchive,
            LoadoutItemGroup.New loadoutGroup,
            ITransaction tx,
            Loadout.ReadOnly loadout,
            CancellationToken cancellationToken)
        {
            var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles()
                .Select(kv => kv.ToLoadoutFile(loadout, loadoutGroup, tx, new GamePath(LocationId.Preferences, kv.Item.Path.DropFirst(1))))
                .ToArray();
            return ValueTask.FromResult<InstallerResult>(files.Length == 0 ? new NotSupported("Archive contains no files") : new Success());
        }
    }
}
