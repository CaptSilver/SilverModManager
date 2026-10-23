using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.DataModel;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Threading;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Unmanaging (and deleting the active loadout) undoes what the loadout deployed, but a config the user
/// had before the game was managed, or edited since, lives outside the game folder and must survive.
/// </summary>
public class UnmanageUserFilesTests(ITestOutputHelper helper) : AStubbedGameTest<UnmanageUserFilesTests>(helper)
{
    private static readonly byte[] SettingsBytes = "[settings]\nvolume=3\n"u8.ToArray();
    private static readonly byte[] ModSettingsBytes = "[settings]\nvolume=11\n"u8.ToArray();
    private static readonly byte[] ModIniBytes = "[mod]\nenabled=1\n"u8.ToArray();

    private readonly HashSet<Hash> _hiddenFromStore = [];
    private readonly ConcurrentQueue<string> _warnings = new();

    private AbsolutePath Settings => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "settings.ini"));
    private AbsolutePath ModIni => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "mod.ini"));

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddLogging(b => b.AddProvider(new WarningCapture(_warnings)))
            .AddSingleton<IFileStore>(sp => new HidingFileStore(sp.GetRequiredService<NxFileStore>(), _hiddenFromStore));
    }

    protected override async Task GenerateGameFiles()
    {
        Settings.Parent.CreateDirectory();
        await Settings.WriteAllBytesAsync(SettingsBytes);
    }

    private async Task<Loadout.ReadOnly> InstallPreferencesMod(Loadout.ReadOnly loadout, Dictionary<RelativePath, byte[]> files)
    {
        await using var archiveFile = await CreateTestArchive(files);
        var archive = await RegisterLocalArchive(archiveFile.Path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new PreferencesInstaller(ServiceProvider));
        return Refresh(loadout);
    }

    /// <summary>
    /// Overrides outrank mods, so a mod only takes over a file the game already had once the override that
    /// ingested it is gone. This drops that override, then installs a mod that ships its own settings.ini.
    /// </summary>
    private async Task<Loadout.ReadOnly> InstallModOverwritingSettings(Loadout.ReadOnly loadout, Dictionary<RelativePath, byte[]> extraFiles)
    {
        var settingsPath = new GamePath(LocationId.Preferences, "settings.ini");
        var ingested = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Single(i => (GamePath)i.TargetPath == settingsPath);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(ingested, recursive: false);
            await tx.Commit();
        }

        var files = new Dictionary<RelativePath, byte[]>(extraFiles) { { "preferences/settings.ini", ModSettingsBytes } };
        loadout = await InstallPreferencesMod(Refresh(loadout), files);
        await Synchronizer.Synchronize(loadout);
        (await Settings.ReadAllBytesAsync()).Should().Equal(ModSettingsBytes);
        return loadout;
    }

    private async Task AssertSettingsHaveOriginalBytes()
    {
        Settings.FileExists.Should().BeTrue("the user's config is not the app's to delete");
        (await Settings.ReadAllBytesAsync()).Should().Equal(SettingsBytes);
    }

    [Fact]
    public async Task UnManage_KeepsPreExistingFileOutsideTheGameFolder()
    {
        await CreateLoadout();

        await LoadoutManager.UnManage(GameInstallation);

        await AssertSettingsHaveOriginalBytes();
    }

    [Fact]
    public async Task UnManage_RemovesDeployedFileButKeepsTheUsersConfig()
    {
        var loadout = await CreateLoadout();
        loadout = await InstallPreferencesMod(loadout, new() { { "preferences/mod.ini", ModIniBytes } });
        await Synchronizer.Synchronize(loadout);
        ModIni.FileExists.Should().BeTrue();

        await LoadoutManager.UnManage(GameInstallation);

        ModIni.FileExists.Should().BeFalse("the loadout deployed it");
        await AssertSettingsHaveOriginalBytes();
    }

    [Fact]
    public async Task UnManage_RestoresTheInitialCopyOfAConfigAModOverwrote()
    {
        var loadout = await CreateLoadout();
        await InstallModOverwritingSettings(loadout, []);

        await LoadoutManager.UnManage(GameInstallation);

        await AssertSettingsHaveOriginalBytes();
    }

    [Fact]
    public async Task UnManage_DeletesInsteadOfRestoringWhenTheInitialCopyIsGone()
    {
        var loadout = await CreateLoadout();
        await InstallModOverwritingSettings(loadout, []);

        _hiddenFromStore.Add(SettingsBytes.xxHash3());
        await LoadoutManager.UnManage(GameInstallation);

        Settings.FileExists.Should().BeFalse("without the original bytes the mod's version must not be left behind as the user's config");
        _warnings.Should().Contain(m => m.Contains("Initial copy") && m.Contains("settings.ini"));
    }

    [Fact]
    public async Task DeleteActiveLoadout_KeepsTheUsersConfigAndRestoresOverwrittenOnes()
    {
        var loadout = await CreateLoadout();
        loadout = await InstallModOverwritingSettings(loadout, new() { { "preferences/mod.ini", ModIniBytes } });

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId);

        ModIni.FileExists.Should().BeFalse();
        await AssertSettingsHaveOriginalBytes();
    }

    /// <summary>
    /// Like the stubbed installer, but drops the <c>preferences</c> folder from the archive path so a mod
    /// can target a file the game already has in its preferences location.
    /// </summary>
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

    /// <summary>
    /// Reports the hashes in <paramref name="hidden"/> as absent, the way a store would after garbage collection.
    /// </summary>
    private class HidingFileStore(IFileStore inner, HashSet<Hash> hidden) : IFileStore
    {
        public ValueTask<bool> HaveFile(Hash hash) => hidden.Contains(hash) ? ValueTask.FromResult(false) : inner.HaveFile(hash);
        public Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default) => inner.BackupFiles(backups, deduplicate, token);
        public Task ExtractFiles(IEnumerable<(Hash Hash, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null) => inner.ExtractFiles(files, token, progress);
        public Task<Dictionary<Hash, byte[]>> ExtractFiles(IEnumerable<Hash> files, CancellationToken token = default) => inner.ExtractFiles(files, token);
        public Task<Stream> GetFileStream(Hash hash, CancellationToken token = default) => inner.GetFileStream(hash, token);
        public Task<byte[]> Load(Hash hash, CancellationToken token = default) => inner.Load(hash, token);
        public AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => inner.WriteLock();
        public void ReloadCaches() => inner.ReloadCaches();
    }

    private class WarningCapture(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);
        public void Dispose() { }

        private class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue(formatter(state, exception));
            }
        }
    }
}
