using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Two installs of one game, and two games that share a Nexus id: installations and loadouts have to
/// tell them apart by the game's own id.
/// </summary>
public class GameKeyedInstallationTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<GameKeyedInstallationTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered first, so a lookup by Nexus id alone finds the wrong game.
        services.AddGame<SiblingStubbedGame>();

        return base.AddServices(services)
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }

    private GameInstallation[] Installs() => GameRegistry.LocateGameInstallations().Where(g => g.Game is StubbedGame).ToArray();

    [Fact]
    public async Task ManageInstallation_GameWithNexusId_WritesGameAndNexusId()
    {
        var metadata = await LoadoutManager.ManageInstallation(Installs()[0]);

        metadata.Game.Value.Should().Be(StubbedGame.GameId);
        GameInstallMetadata.GameId.TryGetValue(metadata, out var nexusId).Should().BeTrue();
        nexusId.Should().Be(StubbedGame.NexusModsGameId.Value);
    }

    [Fact]
    public async Task TryGetMetadata_TwoInstallsOfOneGameAndStore_GetTheirOwnRows()
    {
        var installs = Installs();
        installs.Should().HaveCount(2);
        installs[0].LocatorResult.Store.Should().Be(installs[1].LocatorResult.Store);

        var first = await LoadoutManager.ManageInstallation(installs[0]);
        var second = await LoadoutManager.ManageInstallation(installs[1]);

        second.Id.Should().NotBe(first.Id, "a second folder is a second install, not the first one again");
        (await LoadoutManager.ManageInstallation(installs[0])).Id.Should().Be(first.Id);
        (await LoadoutManager.ManageInstallation(installs[1])).Id.Should().Be(second.Id);

        GameRegistry.TryGetMetadata(installs[0], out var foundFirst).Should().BeTrue();
        foundFirst.Id.Should().Be(first.Id);
        GameRegistry.TryGetMetadata(installs[1], out var foundSecond).Should().BeTrue();
        foundSecond.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task TryGetGameInstallation_TwoInstallsOfOneGameAndStore_ResolvesEachLoadoutToItsInstall()
    {
        var installs = Installs();
        var first = await SeedLoadout(await LoadoutManager.ManageInstallation(installs[0]));
        var second = await SeedLoadout(await LoadoutManager.ManageInstallation(installs[1]));

        GameRegistry.TryGetGameInstallation(first, out var foundFirst).Should().BeTrue();
        foundFirst.Should().Be(installs[0]);
        GameRegistry.TryGetGameInstallation(second, out var foundSecond).Should().BeTrue();
        foundSecond.Should().Be(installs[1]);
    }

    [Fact]
    public async Task TryGetMetadata_SecondInstallMoved_ReattachesItsRowAndRecordsTheOldFolder()
    {
        var installs = Installs();
        var first = await LoadoutManager.ManageInstallation(installs[0]);
        var second = await LoadoutManager.ManageInstallation(installs[1]);
        await SetStoredPath(second, "/mnt/old-library/second");

        GameRegistry.TryGetMetadata(installs[1], out var found).Should().BeTrue();

        found.Id.Should().Be(second.Id);
        var row = await MovedRows.RowOnceItNames(Connection, second.Id, FolderOf(installs[1]));
        GameInstallMetadata.MovedFrom.TryGetValue(row, out var movedFrom).Should().BeTrue();
        movedFrom.Should().Be("/mnt/old-library/second");
        GameInstallMetadata.Load(Connection.Db, first.Id).Path.Should().Be(FolderOf(installs[0]), "the other install's row is not touched");
    }

    [Fact]
    public async Task TryGetGameInstallation_SecondInstallMoved_ResolvesItsLoadoutToTheNewFolder()
    {
        var installs = Installs();
        var first = await SeedLoadout(await LoadoutManager.ManageInstallation(installs[0]));
        var secondMetadata = await LoadoutManager.ManageInstallation(installs[1]);
        var second = await SeedLoadout(secondMetadata);
        await SetStoredPath(secondMetadata, "/mnt/old-library/second");
        second = Loadout.Load(Connection.Db, second.Id);

        GameRegistry.TryGetGameInstallation(second, out var foundSecond).Should().BeTrue();
        foundSecond.Should().Be(installs[1]);
        GameRegistry.TryGetGameInstallation(first, out var foundFirst).Should().BeTrue();
        foundFirst.Should().Be(installs[0]);

        await MovedRows.RowOnceItNames(Connection, secondMetadata.Id, FolderOf(installs[1]));
    }

    [Fact]
    public async Task TryGetMetadata_TwoInstallsAndTwoStaleRows_LeavesBothRowsAlone()
    {
        var installs = Installs();
        var first = await LoadoutManager.ManageInstallation(installs[0]);
        var second = await LoadoutManager.ManageInstallation(installs[1]);
        await SetStoredPath(first, "/mnt/old-library/first");
        await SetStoredPath(second, "/mnt/old-library/second");

        GameRegistry.TryGetMetadata(installs[0], out _).Should().BeFalse("either install could be either row");
        GameRegistry.TryGetMetadata(installs[1], out _).Should().BeFalse();

        GameInstallMetadata.Load(Connection.Db, first.Id).Path.Should().Be("/mnt/old-library/first");
        GameInstallMetadata.Load(Connection.Db, second.Id).Path.Should().Be("/mnt/old-library/second");
        GameInstallMetadata.MovedFrom.TryGetValue(GameInstallMetadata.Load(Connection.Db, first.Id), out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryGetMetadata_RowAtItsInstall_IsNotMarkedAsMoved()
    {
        var installs = Installs();
        var first = await LoadoutManager.ManageInstallation(installs[0]);

        GameRegistry.TryGetMetadata(installs[0], out _).Should().BeTrue();

        GameInstallMetadata.MovedFrom.TryGetValue(GameInstallMetadata.Load(Connection.Db, first.Id), out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryGetMetadata_CalledFromADatabaseObserver_FollowsTheMoveWithoutHanging()
    {
        // Loadout pipelines resolve installations while reacting to new revisions, so the first lookup after a
        // move can come from an observer of the database.
        var installs = Installs();
        await LoadoutManager.ManageInstallation(installs[0]);
        var metadata = await LoadoutManager.ManageInstallation(installs[1]);
        var lookup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = Connection.Revisions.Subscribe(db =>
        {
            if (lookup.Task.IsCompleted || GameInstallMetadata.Load(db, metadata.Id).Path != "/mnt/other-library/game") return;
            try
            {
                lookup.TrySetResult(GameRegistry.TryGetMetadata(installs[1], out _));
            }
            catch (Exception e)
            {
                lookup.TrySetException(e);
            }
        });

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(metadata.Id, GameInstallMetadata.Path, "/mnt/other-library/game");
            // The commit completes only once its observers return, so a lookup that blocks on a write hangs here.
            await tx.Commit().WaitAsync(TimeSpan.FromSeconds(10));
        }

        (await lookup.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var moved = await MovedRows.RowOnceItNames(Connection, metadata.Id, FolderOf(installs[1]));
        GameInstallMetadata.MovedFrom.TryGetValue(moved, out var movedFrom).Should().BeTrue();
        movedFrom.Should().Be("/mnt/other-library/game");
    }

    private static string FolderOf(GameInstallation installation) => installation.Locations[LocationId.Game].Path.ToString();

    private async Task SetStoredPath(GameInstallMetadata.ReadOnly metadata, string path)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(metadata.Id, GameInstallMetadata.Path, path);
        await tx.Commit();
    }

    [Fact]
    public async Task LoadoutGame_TwoGamesShareANexusId_ResolvesByGameId()
    {
        var loadout = await SeedLoadout(await LoadoutManager.ManageInstallation(Installs()[0]));

        loadout.Game.Should().BeOfType<StubbedGame>();
    }

    private async Task<Loadout.ReadOnly> SeedLoadout(GameInstallMetadata.ReadOnly metadata)
    {
        using var tx = Connection.BeginTransaction();
        var loadout = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }
}

/// <summary>
/// A game without a Nexus id has nothing but its own id to be found by.
/// </summary>
public class NoNexusIdInstallationTests(ITestOutputHelper helper) : AIsolatedGameTest<NoNexusIdInstallationTests, NoNexusIdStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<NoNexusIdStubbedGame>()
            .AddUniversalGameLocator<NoNexusIdStubbedGame>(Version.Parse("0.0.0"));
    }

    [Fact]
    public async Task ManageInstallation_GameWithoutNexusId_WritesGameAndNoNexusId()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);

        metadata.Game.Value.Should().Be(NoNexusIdStubbedGame.GameId);
        metadata.Contains(GameInstallMetadata.GameId).Should().BeFalse();
        GameRegistry.TryGetMetadata(GameInstallation, out var found).Should().BeTrue();
        found.Id.Should().Be(metadata.Id);
    }

    [Fact]
    public async Task TryGetMetadata_InstallMovedToAnotherPath_StillFindsTheRow()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);

        // Steam moved the game to another library: same game, same store, different folder.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(metadata.Id, GameInstallMetadata.Path, "/mnt/other-library/game");
            await tx.Commit();
        }

        GameRegistry.TryGetMetadata(GameInstallation, out var found).Should().BeTrue();
        found.Id.Should().Be(metadata.Id);
    }

    [Fact]
    public async Task TryGetMetadata_InstallMovedToAnotherPath_UpdatesTheStoredPathOnce()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var current = GameInstallation.Locations[LocationId.Game].Path.ToString();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(metadata.Id, GameInstallMetadata.Path, "/mnt/other-library/game");
            await tx.Commit();
        }

        GameRegistry.TryGetMetadata(GameInstallation, out var found).Should().BeTrue();

        found.Id.Should().Be(metadata.Id);
        GameInstallMetadata.MovedFrom.TryGetValue(await MovedRows.RowOnceItNames(Connection, metadata.Id, current), out var movedFrom).Should().BeTrue();
        movedFrom.Should().Be("/mnt/other-library/game");

        var txBefore = Connection.Db.BasisTxId;
        GameRegistry.TryGetMetadata(GameInstallation, out _).Should().BeTrue();
        Connection.Db.BasisTxId.Should().Be(txBefore, "a row already at the install's folder is not written again");
    }

    [Fact]
    public async Task LoadoutGame_ResolvesWithoutNexusId()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "No Nexus Id");

        loadout.Game.Should().BeOfType<NoNexusIdStubbedGame>();
        loadout.InstallationInstance.Should().Be(GameInstallation);
    }
}

internal static class MovedRows
{
    /// <summary>
    /// The registry records a move in the background, so a test reads the row once the write has landed.
    /// </summary>
    public static async Task<GameInstallMetadata.ReadOnly> RowOnceItNames(IConnection connection, EntityId id, string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var row = GameInstallMetadata.Load(connection.Db, id);
            if (row.Path == path) return row;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Row {id} still names `{row.Path}`, not `{path}`");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }
}
