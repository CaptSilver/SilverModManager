using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// An apply that removes files from the game folder says so in the log: how many, which folders, and which paths.
/// </summary>
public class ApplyVisibilityTests(ITestOutputHelper helper) : AStubbedGameTest<ApplyVisibilityTests>(helper)
{
    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => _logs.AddTo(base.AddServices(services));

    private static readonly RelativePath[] BigModFiles =
    [
        "mods/big/a.txt", "mods/big/b.txt", "mods/big/sub/c.txt", "mods/big/sub/d.txt", "mods/big/sub/e.txt",
    ];

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Mod)> AppliedLoadoutWithBigMod()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, BigModFiles, loadout.LoadoutId, "Big Mod");
            await tx.Commit();
        }

        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var mod = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Big Mod");
        return (Refresh(loadout), mod);
    }

    private async Task SwitchOff(LoadoutItemGroup.ReadOnly mod)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(mod.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private IEnumerable<CapturedLine> At(LogLevel level) => _logs.Lines.Where(l => l.Level == level);

    [Fact]
    public async Task AnApplyThatRemovesAModLogsHowManyFilesWentAndFromWhere()
    {
        var (loadout, mod) = await AppliedLoadoutWithBigMod();
        await SwitchOff(mod);

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var summary = At(LogLevel.Information).Select(l => l.Message).Where(m => m.Contains("deleted 5 files")).Should().ContainSingle().Subject;
        summary.Should().Contain("mods/big", "the folders with the most deletions are named");
    }

    [Fact]
    public async Task EachDeletedPathIsLoggedAtTrace()
    {
        var (loadout, mod) = await AppliedLoadoutWithBigMod();
        await SwitchOff(mod);

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var traced = At(LogLevel.Trace).Select(l => l.Message).Where(m => m.StartsWith("Deleting ")).ToArray();
        foreach (var file in BigModFiles) traced.Should().Contain(m => m.Contains(file.Path), $"{file} was deleted");
    }

    [Fact]
    public async Task FoldersTheApplyEmptiedAreLoggedWhenTheyAreRemoved()
    {
        var (loadout, mod) = await AppliedLoadoutWithBigMod();
        await SwitchOff(mod);

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        At(LogLevel.Information).Select(l => l.Message).Should().Contain(m => m.StartsWith("Removed the empty folder") && m.EndsWith("mods"));
        (GameInstallation.Locations[LocationId.Game].Path / "mods").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task AnApplyThatDeletesNothingSaysSo()
    {
        var (loadout, _) = await AppliedLoadoutWithBigMod();

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        At(LogLevel.Information).Select(l => l.Message).Should().Contain(m => m.StartsWith("Sync of loadout") && m.Contains("deleted 0 files"));
    }

    [Fact]
    public async Task PuttingTheFolderBackToVanillaLogsWhatItDeleted()
    {
        var (loadout, _) = await AppliedLoadoutWithBigMod();

        await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        At(LogLevel.Information).Select(l => l.Message).Should().Contain(m => m.StartsWith("Reset of the game folder") && m.Contains("deleted 5 files"));
    }
}
