using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Switching a mod, collection or group on or off changes what the next apply puts in the game folder, so the log says
/// which item it was and in which loadout, whichever screen or command made the change.
/// </summary>
public class LoadoutSwitchLogTests(ITestOutputHelper helper) : AStubbedGameTest<LoadoutSwitchLogTests>(helper)
{
    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => _logs.AddTo(base.AddServices(services));

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId[] Mods)> LoadoutWithMods(int count)
    {
        var loadout = await CreateLoadout();
        var ids = new List<LoadoutItemGroupId>();
        using (var tx = Connection.BeginTransaction())
        {
            for (var i = 0; i < count; i++)
                await AddModAsync(tx, [(RelativePath)$"mods/mod{i}/file.txt"], loadout.LoadoutId, $"Mod {i}");
            await tx.Commit();
        }

        var names = Enumerable.Range(0, count).Select(i => $"Mod {i}").ToHashSet();
        var mods = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Where(g => names.Contains(g.AsLoadoutItem().Name)).Select(g => g.LoadoutItemGroupId).ToArray();
        return (loadout, mods);
    }

    private async Task Switch(IEnumerable<LoadoutItemGroupId> items, bool on)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var id in items)
        {
            if (on) tx.RetractCurrent(id.Value, LoadoutItem.Disabled);
            else tx.Add(id.Value, LoadoutItem.Disabled, Null.Instance);
        }
        await tx.Commit();
    }

    private async Task<string[]> SwitchLines(int expected)
    {
        // The log is written from the revision stream, which can trail the commit by a moment.
        for (var i = 0; i < 100; i++)
        {
            var lines = _logs.Lines.Where(l => l.Message.StartsWith("Switched o")).Select(l => l.Message).ToArray();
            if (lines.Length >= expected) return lines;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return _logs.Lines.Where(l => l.Message.StartsWith("Switched o")).Select(l => l.Message).ToArray();
    }

    [Fact]
    public async Task SwitchingAModOffNamesTheModAndTheLoadout()
    {
        var (loadout, mods) = await LoadoutWithMods(1);

        await Switch(mods, on: false);

        var line = (await SwitchLines(1)).Should().ContainSingle().Subject;
        line.Should().StartWith("Switched off mod 'Mod 0'").And.Contain(loadout.Name).And.Contain(loadout.LoadoutId.ToString());
        _logs.Lines.Single(l => l.Message == line).Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task SwitchingItBackOnSaysSo()
    {
        var (_, mods) = await LoadoutWithMods(1);
        await Switch(mods, on: false);
        await SwitchLines(1);

        await Switch(mods, on: true);

        (await SwitchLines(2)).Last().Should().StartWith("Switched on mod 'Mod 0'");
    }

    [Fact]
    public async Task ManyItemsAtOnceAreOneLineThatNamesSomeOfThem()
    {
        var (_, mods) = await LoadoutWithMods(8);
        _logs.Lines.Count(l => l.Message.StartsWith("Switched o")).Should().Be(0);

        await Switch(mods, on: false);

        var line = (await SwitchLines(1)).Should().ContainSingle().Subject;
        line.Should().StartWith("Switched off 8 items").And.Contain("Mod 0");
    }

    [Fact]
    public async Task DeletingASwitchedOffModIsNotLoggedAsSwitchingItOn()
    {
        var (_, mods) = await LoadoutWithMods(1);
        await Switch(mods, on: false);
        await SwitchLines(1);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(mods[0].Value, recursive: true);
            await tx.Commit();
        }
        await Task.Delay(300, TestContext.Current.CancellationToken);

        _logs.Lines.Where(l => l.Message.StartsWith("Switched on")).Should().BeEmpty();
    }
}
