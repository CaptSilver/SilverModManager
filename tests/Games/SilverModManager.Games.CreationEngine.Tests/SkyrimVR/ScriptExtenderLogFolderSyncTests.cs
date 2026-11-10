using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimVR;

/// <summary>
/// The script extender's log folder belongs to the game: an Apply neither ingests what the loader, its plugins or the
/// crash logger write there, nor deletes a file an earlier build filed into the Overrides.
/// </summary>
public class ScriptExtenderLogFolderSyncTests(ITestOutputHelper helper)
    : AIsolatedGameTest<ScriptExtenderLogFolderSyncTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return _logs.AddTo(base.AddServices(services)
            .AddDiagnosticWriter()
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimVR.SkyrimVR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create));
    }

    private AbsolutePath Pref(string rel) => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, rel));

    private static IEnumerable<string> OverridesPaths(Loadout.ReadOnly l) =>
        LoadoutOverridesGroup.FindByOverridesFor(l.Db, l.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(f => ((GamePath)f.TargetPath).ToString());

    private async Task WriteGameFile(string rel, string content)
    {
        var file = Pref(rel);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    // An Overrides row an earlier build filed, with the disk state the sync would have recorded for it. The file holds
    // its own relative path, because that is what AddToOverrides hashes for the row.
    private async Task SeedIngested(Loadout.ReadOnly loadout, string rel)
    {
        var content = rel;
        var path = new GamePath(LocationId.Preferences, rel);
        await WriteGameFile(rel, content);
        await AddToOverrides(loadout, path);
        var bytes = Encoding.UTF8.GetBytes(content);
        using var tx = Connection.BeginTransaction();
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = path.ToGamePathParentTuple(loadout.Installation),
            Identity = FileIdentity.XxHash3(bytes.xxHash3()),
            XxHash3 = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LastModified = DateTime.UtcNow,
            GameId = loadout.Installation,
        };
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadout, loadout.Id);
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        await tx.Commit();
    }

    [Fact]
    public async Task AFileTheGameWroteUnderSkseIsNotIngestedByApply()
    {
        var files = new Dictionary<string, string>
        {
            ["SKSE/crash-x.log"] = "crash log body",
            ["SKSE/MergeMapper.log"] = "merge mapper body",
        };
        foreach (var (rel, content) in files) await WriteGameFile(rel, content);

        var loadout = await Synchronizer.Synchronize(await CreateLoadout());

        OverridesPaths(loadout).Should().NotContain(p => p.Contains("SKSE/"));
        foreach (var (rel, content) in files)
        {
            (await FileStore.HaveFile(FileIdentity.XxHash3(Encoding.UTF8.GetBytes(content).xxHash3()))).Should().BeFalse(rel);
            Pref(rel).FileExists.Should().BeTrue(rel);
            (await Pref(rel).ReadAllTextAsync()).Should().Be(content);
        }
    }

    [Fact]
    public async Task AnAlreadyIngestedLogIsRetiredAndLeftOnDisk()
    {
        var loadout = await CreateLoadout();
        await SeedIngested(loadout, "SKSE/crash-x.log");

        var applied = await Synchronizer.Synchronize(Refresh(loadout));

        OverridesPaths(applied).Should().NotContain(p => p.Contains("crash-x.log"));
        Pref("SKSE/crash-x.log").FileExists.Should().BeTrue();
        (await Pref("SKSE/crash-x.log").ReadAllTextAsync()).Should().Be("SKSE/crash-x.log");
        DiskStateEntry.FindByGame(applied.Db, applied.Installation).Select(e => ((GamePath)e.Path).Path.ToString())
            .Should().NotContain(p => p.StartsWith("SKSE/"));
        _logs.Lines.Should().NotContain(e => e.Level >= LogLevel.Warning && e.Message.Contains("crash-x.log"));
    }

    [Fact]
    public async Task AnAlreadyIngestedJsonIsNotDeletedByTheFirstApply()
    {
        var loadout = await CreateLoadout();
        await SeedIngested(loadout, "SKSE/OCUGestureSpellList.json");

        var applied = await Synchronizer.Synchronize(Refresh(loadout));

        OverridesPaths(applied).Should().NotContain(p => p.Contains("OCUGestureSpellList"));
        Pref("SKSE/OCUGestureSpellList.json").FileExists.Should().BeTrue("the game wrote it, so the sync must not delete it");
    }

    private static readonly DiagnosticId ProtectedPathId = new("SilverModManager.Games.Generic", number: 4);
    private static readonly DiagnosticId GameWrittenPathId = new("SilverModManager.Games.Generic", number: 8);

    // A mod's own file under a protected path: the emitter files it under "the game writes this itself" or under
    // "allow protected paths" depending on IsGameWrittenPath.
    private async Task<Diagnostic[]> ProtectedPathDiagnostics(string rel)
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "Some Mod");
            AddFile(tx, loadout, group, new GamePath(LocationId.Preferences, rel), content: rel);
            await tx.Commit();
        }

        var emitter = Game.DiagnosticEmitters.OfType<ProtonPrefixEmitter>().Single();
        var diagnostics = await emitter.Diagnose(Refresh(loadout), CancellationToken.None).ToArrayAsync();
        return diagnostics.Where(d => d.Id == ProtectedPathId || d.Id == GameWrittenPathId).ToArray();
    }

    [Fact]
    public async Task AModFileUnderSkseIsReportedAsGameWrittenNotAsProtected()
    {
        var diagnostics = await ProtectedPathDiagnostics("SKSE/x.json");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(GameWrittenPathId);
        diagnostics[0].FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("SKSE/x.json");
    }

    [Fact]
    public async Task AModSaveFileIsStillReportedAsProtected()
    {
        var diagnostics = await ProtectedPathDiagnostics("Saves/x.fos");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(ProtectedPathId);
    }
}
