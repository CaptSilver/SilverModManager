using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A double click runs a toggle twice before either commit lands, so both decide from the same database. The
/// second must not fail because the first already cleared the flag.
/// </summary>
public class DoubleToggleTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<LoadoutItemId> AddItem(IWritableAttribute<Null> flag)
    {
        using var tx = Connection.BeginTransaction();
        var id = tx.TempId();
        _ = new LoadoutItem.New(tx, id)
        {
            Name = "Some mod",
            LoadoutId = LoadoutId.From(EntityId.From(0xDEAD)),
        };
        tx.Add(id, flag, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemId.From(result[id]);
    }

    // Tx functions run on the store's one writer thread. Holding one there keeps both calls' commits from landing
    // until both have read the database.
    private async Task TwiceOnOneSnapshot(Func<Task> toggle)
    {
        using var writerHeld = new ManualResetEventSlim();
        Task held;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(writerHeld, static (_, _, gate) => gate.Wait(TimeSpan.FromSeconds(30)));
            held = tx.Commit();
        }

        var first = toggle();
        var second = toggle();
        writerHeld.Set();
        await held;
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task ClearingProtectedPathsTwiceAtOnce_Succeeds()
    {
        var id = await AddItem(LoadoutItem.ProtectedPathsAllowed);

        await TwiceOnOneSnapshot(() => LoadoutViewModel.HandleToggleProtectedPathsAllowed([id], Connection));

        LoadoutItem.Load(Connection.Db, id).IsProtectedPathsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task EnablingAnItemTwiceAtOnce_Succeeds()
    {
        var id = await AddItem(LoadoutItem.Disabled);

        await TwiceOnOneSnapshot(() => LoadoutViewModel.HandleToggleItemEnabledState([id], Connection));

        LoadoutItem.Load(Connection.Db, id).IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingATweakOnTwiceAtOnce_Succeeds()
    {
        var id = await AddItem(LoadoutItem.Disabled);

        await TwiceOnOneSnapshot(() => IniEdits.SetTweakSwitch(Connection, id, on: true));

        LoadoutItem.Load(Connection.Db, id).IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task DismissingAMovedGameTwiceAtOnce_Succeeds()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Moved");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Installation.Id, GameInstallMetadata.MovedFrom, "/old/library/game");
            await tx.Commit();
        }
        var handler = new DismissGameMovedActionHandler(Connection, Provider.GetRequiredService<IDiagnosticManager>());

        await TwiceOnOneSnapshot(() => handler.ExecuteAsync(loadout.LoadoutId, []));

        GameInstallMetadata.MovedFrom.Contains(GameInstallMetadata.Load(Connection.Db, loadout.Installation.Id)).Should().BeFalse();
    }
}
