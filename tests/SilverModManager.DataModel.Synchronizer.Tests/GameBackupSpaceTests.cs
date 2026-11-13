using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.Diagnostics;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The backups the app keeps of a game's own files are unbounded, so the health check warns once they pass the backup
/// limit and offers to drop the ones no loadout needs to put the game folder back.
/// </summary>
public class GameBackupSpaceTests(ITestOutputHelper helper) : AStubbedGameTest<GameBackupSpaceTests>(helper)
{
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton(new BackupLimit(Size.KB * 8))
            .AddSingleton<IBackupOverflowPrompt>(_prompt);
    }

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private GameInstallMetadataId Install => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private GameBackupSpace Space => ServiceProvider.GetRequiredService<GameBackupSpace>();

    private void WriteExternalFiles(int count, int eachBytes)
    {
        GameDir.CreateDirectory();
        for (var i = 0; i < count; i++)
        {
            var bytes = new byte[eachBytes];
            Array.Fill(bytes, (byte)(i + 1));
            File.WriteAllBytes((GameDir / $"ext{i}.bin").ToString(), bytes);
        }
    }

    /// <summary>Stores a file no loadout, disk state or mod refers to and roots it as a game-file backup, the way an earlier game version's backup lingers.</summary>
    private async Task<FileIdentity> StoreStrayBackup(int size)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)0xEE);
        var identity = FileIdentity.XxHash3(bytes.xxHash3());
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("stray", new MemoryStream(bytes)), identity, Size.FromLong(size))]);
        await GameBackedUpFile.Root(Connection, Install, [identity]);
        return identity;
    }

    private async Task<List<Diagnostic>> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = new BackupSpaceEmitter(Space, ServiceProvider.GetRequiredService<BackupLimit>());
        return await emitter.Diagnose(Loadout.Load(Connection.Db, loadout.LoadoutId), CancellationToken.None).ToListAsync();
    }

    private HashSet<ulong> RootedHashes() => GameBackedUpFile.All(Connection.Db)
        .Where(f => f.GameInstallId == Install).Select(f => f.Hash.Value).ToHashSet();

    [Fact]
    public async Task BackupsUnderTheLimit_GetNoDiagnostic()
    {
        WriteExternalFiles(4, 1024);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var loadout = await CreateLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BackupsOverTheLimit_GetOneDiagnosticWithTheirSize()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var loadout = await CreateLoadout();

        var report = await Space.Measure(Install);
        var diagnostics = await Diagnose(loadout);

        report.Total.Value.Should().BeGreaterThanOrEqualTo(4 * 4096UL, "every external file was backed up");
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.FormatSummary(new PlainDiagnosticWriter()).Should().Contain(BackupOverflow.FormatSize(report.Total));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task BackupsEveryLoadoutStillNeeds_OfferNoCleanup()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var loadout = await CreateLoadout();

        var report = await Space.Measure(Install);
        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        report.Removable.Should().Be(Size.Zero);
        diagnostic.Actions.Should().BeEmpty("nothing can go without losing a file the loadout restores");
    }

    [Fact]
    public async Task OnlyOneLoadoutOfTheGame_CarriesTheDiagnostic()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var first = await CreateLoadout();
        var second = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        var count = (await Diagnose(first)).Count + (await Diagnose(second)).Count;

        count.Should().Be(1, "the backups belong to the game, not to a loadout");
    }

    [Fact]
    public async Task AnUnneededBackup_IsReportedAsRemovableAndOffersTheCleanup()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var loadout = await CreateLoadout();
        await StoreStrayBackup(3000);

        var report = await Space.Measure(Install);
        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        report.Removable.Should().Be(Size.FromLong(3000));
        report.RemovableCount.Should().Be(1);
        diagnostic.Actions.Should().ContainSingle().Which.Id.Should().Be(DiagnosticActions.CleanGameFileBackupsId);
        diagnostic.FormatDetails(new PlainDiagnosticWriter()).Should().Contain(BackupOverflow.FormatSize(report.Removable));
    }

    [Fact]
    public async Task Cleanup_RemovesTheUnneededBackupAndKeepsTheOnesALoadoutNeeds()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        await CreateLoadout();
        var stray = await StoreStrayBackup(3000);
        var kept = RootedHashes().Where(h => h != stray.XxHash3().Value).ToList();
        kept.Should().NotBeEmpty("the loadout's backups are rooted");

        var removed = await Space.RemoveUnneeded(Install);

        removed.Removable.Should().Be(Size.FromLong(3000));
        RootedHashes().Should().NotContain(stray.XxHash3().Value);
        RootedHashes().Should().BeEquivalentTo(kept, "a loadout still needs every one of these to put the folder back");
        (await FileStore.HaveFile(stray)).Should().BeFalse("nothing roots it any more, so the collection took it");
        foreach (var hash in kept)
            (await FileStore.HaveFile(FileIdentity.XxHash3(Hash.From(hash)))).Should().BeTrue();
        (await Space.Measure(Install)).Removable.Should().Be(Size.Zero);
    }

    [Fact]
    public async Task ABackupOnlyTheVanillaStateLoadoutNames_IsKept()
    {
        WriteExternalFiles(2, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        await CreateLoadout();
        var stray = await StoreStrayBackup(3000);
        var vanilla = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, vanilla.LoadoutId, "Original files");
            var bytes = new byte[3000];
            Array.Fill(bytes, (byte)0xEE);
            AddFile(tx, vanilla.LoadoutId, group, new GamePath(LocationId.Game, "original.bin"), bytes, out var identity, out _);
            identity.Should().Be(stray);
            tx.Add(vanilla.LoadoutId, Loadout.LoadoutKind, LoadoutKind.VanillaState);
            await tx.Commit();
        }

        var removed = await Space.RemoveUnneeded(Install);

        removed.RemovableCount.Should().Be(0, "the vanilla state loadout puts this file back");
        RootedHashes().Should().Contain(stray.XxHash3().Value);
    }

    [Fact]
    public async Task Cleanup_LeavesTheBackupsOfAnotherGameInstallAlone()
    {
        WriteExternalFiles(2, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        await CreateLoadout();
        var bytes = Encoding.UTF8.GetBytes("another game's backup");
        var otherIdentity = FileIdentity.XxHash3(bytes.xxHash3());
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("other", new MemoryStream(bytes)), otherIdentity, Size.FromLong(bytes.Length))]);
        using (var tx = Connection.BeginTransaction())
        {
            var other = new GameInstallMetadata.New(tx)
            {
                Store = GameStore.GOG,
                Path = "/nowhere/other-game",
                GameId = NexusModsGameId.From(4321),
                Game = Sdk.Games.GameId.From("OtherGame"),
                Name = "Other game",
            };
            _ = new GameBackedUpFile.New(tx) { Hash = otherIdentity.XxHash3(), GameInstallId = other };
            await tx.Commit();
        }

        await Space.RemoveUnneeded(Install);

        GameBackedUpFile.All(Connection.Db).Select(f => f.Hash.Value).Should().Contain(otherIdentity.XxHash3().Value);
    }
}
