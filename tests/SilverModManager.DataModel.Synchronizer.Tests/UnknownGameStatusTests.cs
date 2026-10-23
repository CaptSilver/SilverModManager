using System.Collections.Concurrent;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// A loadout whose game this build does not know must report a status without taking down the status stream.
/// </summary>
public class UnknownGameStatusTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<UnknownGameStatusTests>(helper)
{
    [Fact]
    public async Task StatusForLoadout_GameNotKnown_EmitsUnavailableAndStaysAlive()
    {
        // The normal loadout comes first so that creating it does not itself run a sync-tree query that sees
        // the unknown one; this way the test also checks that the query for the normal loadout survives it.
        var normal = await CreateLoadout();
        var unknown = await SeedLoadoutOnUnknownGame();

        var unknownStatuses = new ConcurrentQueue<LoadoutSynchronizerState>();
        var unknownErrors = new ConcurrentQueue<Exception>();
        var normalStatuses = new ConcurrentQueue<LoadoutSynchronizerState>();

        var unknownStream = await SynchronizerService.StatusForLoadout(unknown.LoadoutId);
        using var unknownSub = unknownStream.Subscribe(unknownStatuses.Enqueue, unknownErrors.Enqueue);
        var normalStream = await SynchronizerService.StatusForLoadout(normal);
        using var normalSub = normalStream.Subscribe(normalStatuses.Enqueue, _ => { });

        await WaitFor(() => unknownStatuses.Count >= 1 && normalStatuses.Count >= 1);
        unknownErrors.Should().BeEmpty();
        unknownStatuses.Last().Should().Be(LoadoutSynchronizerState.Unavailable);

        // a later change to the unknown loadout still emits
        var before = unknownStatuses.Count;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(unknown.Id, Loadout.Name, "Renamed");
            await tx.Commit();
        }
        await WaitFor(() => unknownStatuses.Count > before);
        unknownErrors.Should().BeEmpty();
        unknownStatuses.Last().Should().Be(LoadoutSynchronizerState.Unavailable);

        // the other loadout keeps getting statuses
        normalStatuses.Last().Should().NotBe(LoadoutSynchronizerState.Unavailable);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Status stream did not emit in time");
            await Task.Delay(25);
        }
    }

    private async Task<Loadout.ReadOnly> SeedLoadoutOnUnknownGame()
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Store = GameInstallation.LocatorResult.Store,
            Path = GameInstallation.Locations[LocationId.Game].Path.ToString() + "-unknown",
            GameId = NexusModsGameId.From(987654),
            Name = "Dropped game",
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Unknown game loadout",
            ShortName = "UNK",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }
}
