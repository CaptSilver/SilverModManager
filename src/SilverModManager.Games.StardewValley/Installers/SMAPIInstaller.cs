using System.Diagnostics;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.StardewValley.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.StardewValley.Installers;

public class SMAPIInstaller : ALibraryArchiveInstaller
{
    private static readonly RelativePath InstallDatFile = "install.dat";
    private static readonly RelativePath LinuxFolder = "linux";
    private static readonly RelativePath WindowsFolder = "windows";

    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IFileStore _fileStore;
    private readonly IFileHashesService _fileHashesService;
    private readonly IStreamSourceDispatcher _streamSource;
    private readonly IFullFileHasher _fullHasher;
    private readonly IFileIdentityResolver _identityResolver;

    public SMAPIInstaller(
        IServiceProvider serviceProvider,
        ILogger<SMAPIInstaller> logger,
        TemporaryFileManager temporaryFileManager,
        IFileHashesService fileHashesService,
        IFileStore fileStore,
        IStreamSourceDispatcher streamSourceDispatcher)
        : base(serviceProvider, logger)
    {
        _temporaryFileManager = temporaryFileManager;
        _fileStore = fileStore;
        _fileHashesService = fileHashesService;
        _streamSource = streamSourceDispatcher;
        _fullHasher = serviceProvider.GetService<IFullFileHasher>() ?? MultiHashFileHasher.Instance;
        _identityResolver = serviceProvider.GetRequiredService<IFileIdentityResolver>();
    }

    public override async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var targetOS = loadout.InstallationInstance.LocatorResult.TargetOS;
        var targetParentName = targetOS.IsWindows ? WindowsFolder : LinuxFolder;

        var foundInstallDataFile = libraryArchive.Children.TryGetFirst(fileEntry =>
        {
            var path = fileEntry.Path;
            var fileName = path.FileName;
            if (!fileName.Equals(InstallDatFile)) return false;
            var parentName = path.Parent.FileName;
            return parentName.Equals(targetParentName);
        }, out var installDataFile);

        if (!foundInstallDataFile) return new NotSupported(Reason: "Found no SMAPI installation data file in the archive");
        if (!installDataFile.AsLibraryFile().TryGetAsLibraryArchive(out var installDataArchive))
        {
            Logger.LogError("Expected Library Item `{LibraryItem}` (`{Id}`) to be an archive", installDataFile.AsLibraryFile().AsLibraryItem().Name, installDataFile.Id);
            return new NotSupported(Reason: "Expected the installation data file to be an archive");
        }

        var isUnix = targetOS.IsUnix();

        // NOTE(erri120): paths can be verified using Steam depots: https://steamdb.info/app/413150/depots/
        RelativePath unixLauncherFile = "StardewValley";

        var modDatabaseEntityId = Optional<EntityId>.None;
        var version = Optional<string>.None;

        foreach (var fileEntry in installDataArchive.Children)
        {
            var to = new GamePath(LocationId.Game, fileEntry.Path);
            var fileName = fileEntry.Path.FileName;

            var entityId = transaction.TempId();
            var loadoutItem = new LoadoutItem.New(transaction, entityId)
            {
                Name = fileName,
                LoadoutId = loadout,
                ParentId = loadoutGroup,
            };

            // NOTE(erri120): This is a more reliable approach for getting
            // the SMAPI version.
            if (!version.HasValue && fileName.Equals("StardewModdingAPI.dll"))
            {
                try
                {
                    await using var tempFile = _temporaryFileManager.CreateFile();
                    await using (var stream = await _fileStore.GetFileStream(fileEntry.AsLibraryFile().Identity, token: cancellationToken))
                    {
                        await using var fs = tempFile.Path.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                        await stream.CopyToAsync(fs, cancellationToken);
                    }

                    var fvi = tempFile.Path.FileInfo.GetFileVersionInfo();
                    version = fvi.FileVersion.ToString(fieldCount: 3);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Exception while getting version of SMAPI from DLL");
                }
            }

            // For Linux & macOS: replace the game launcher executable "StardewValley" with "unix-launcher.sh"
            // https://github.com/Pathoschild/SMAPI/blob/5919337236650c6a0d7755863d35b2923a94775c/src/SMAPI.Installer/InteractiveInstaller.cs#L395-L425
            if (isUnix && fileName.Equals("unix-launcher.sh"))
            {
                to = new GamePath(LocationId.Game, unixLauncherFile);
            }

            // NOTE(erri120): The official installer doesn't replace "Stardew Valley.exe" with
            // "StardewModdingAPI.exe" to allow players to run the vanilla game without having
            // to uninstall SMAPI. However, we don't need this behavior.
            // https://github.com/Nexus-Mods/NexusMods.App/issues/1012#issuecomment-1971039971
            if (!isUnix && fileName.Equals("StardewModdingAPI.exe"))
            {
                to = new GamePath(LocationId.Game, "Stardew Valley.exe");
            }

            var loadoutFile = new LoadoutFile.New(transaction, entityId)
            {
                Hash = fileEntry.AsLibraryFile().Hash,
                Size = fileEntry.AsLibraryFile().Size,
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, entityId)
                {
                    TargetPath = to.ToGamePathParentTuple(loadout.Id),
                    LoadoutItem = loadoutItem,
                },
            };

            if (!modDatabaseEntityId.HasValue &&
                fileName.Equals("metadata.json") &&
                fileEntry.Path.Parent.FileName.Equals("smapi-internal"))
            {
                _ = new SMAPIModDatabaseLoadoutFile.New(transaction, entityId)
                {
                    IsModDatabaseFile = true,
                    LoadoutFile = loadoutFile,
                };

                modDatabaseEntityId = entityId;
            }
        }

        // copy the game file "Stardew Valley.deps.json" to "StardewModdingAPI.deps.json"
        // https://github.com/Pathoschild/SMAPI/blob/9763bc7484e29cbc9e7f37c61121d794e6720e75/src/SMAPI.Installer/InteractiveInstaller.cs#L419-L425
        var srcPath = new GamePath(LocationId.Game, "Stardew Valley.deps.json");
        await _fileHashesService.GetFileHashesDb();

        if (await TryGetGameFile(srcPath, loadout, cancellationToken) is not { } gameFile)
        {
            Logger.LogWarning("Can't find the file `{Path}` SMAPI might not work properly", srcPath);
        }
        else
        {
            var (fileIdentity, fileSize, onDisk) = gameFile;
            var fileHash = fileIdentity.XxHash3();
            var to = new GamePath(LocationId.Game, "StardewModdingAPI.deps.json");
            _ = new LoadoutFile.New(transaction, out var id)
            {
                Hash = fileHash,
                Size = fileSize,
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = to.ToGamePathParentTuple(loadout.Id),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = to.FileName,
                        LoadoutId = loadout,
                        ParentId = loadoutGroup,
                    },
                },
            };

            if (!await _fileStore.HaveFile(fileIdentity))
            {
                // A file read from disk is archived from disk: the stream sources find a game file by the xxHash3 of
                // a disk state row, which a file the store vouched for does not have until something reads it.
                IStreamFactory streamFactory = onDisk is { } file
                    ? new NativeFileStreamFactory(file)
                    : new SourceDispatcherStreamFactory(to.FileName, fileIdentity, _streamSource);
                await _fileStore.BackupFiles([new ArchivedFileEntry(streamFactory, fileIdentity, fileSize)], deduplicate: false, token: cancellationToken);
            }
        }

        _ = new SMAPILoadoutItem.New(transaction, loadoutGroup.Id)
        {
            LoadoutItemGroup = loadoutGroup,
            Version = version.ValueOrDefault(),
            ModDatabaseId = modDatabaseEntityId.ValueOrDefault(),
        };

        return new Success();
    }

    /// <summary>
    /// The game file's store key and size, and the file on disk when the key had to be read from it.
    /// </summary>
    internal readonly record struct GameFileSource(FileIdentity Identity, Size Size, AbsolutePath? OnDisk);

    private async ValueTask<GameFileSource?> TryGetGameFile(GamePath path, Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (_fileHashesService.GetGameFiles(loadout.InstallationInstance, loadout.LocatorIds)
            .Where(f => f.Path == path)
            .TryGetFirst(out var gameFileRecord))
        {
            var file = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
            var source = await FromRecord(gameFileRecord, file, loadout.InstallationInstance, cancellationToken);
            if (source is not null) return source;

            Logger.LogWarning("The game file hashes list `{Path}` but it is missing on disk, using Loadout as fallback", path);
        }
        else
        {
            Logger.LogWarning("Failed to find game file `{Path}` in the game file hashes, using Loadout as fallback", path);
        }

        var entities = LoadoutItemWithTargetPath.FindByTargetPath(loadout.Db, path.ToGamePathParentTuple(loadout.Id));
        if (!entities.TryGetFirst(x => x.IsLoadoutFile(), out var entity)) return null;
        Logger.LogInformation("Using game file `{Path}` found in the Loadout", path);

        var loadoutFile = LoadoutFile.Load(loadout.Db, entity.Id);
        Debug.Assert(loadoutFile.IsValid());

        return new GameFileSource(FileIdentity.XxHash3(loadoutFile.Hash), loadoutFile.Size, null);
    }

    /// <summary>
    /// A recorded game file as the library and file store know it, which is by xxHash3. A record known by another
    /// digest is joined to the xxHash3 the store holds through a recorded relation, so nothing is read. Only a
    /// record no relation reaches is read once and its attestation settled. Null when that file is not on disk.
    /// </summary>
    internal async ValueTask<GameFileSource?> FromRecord(GameFileRecord record, AbsolutePath file, GameInstallation installation, CancellationToken cancellationToken)
    {
        if (record.Identity.Kind == HashKind.XxHash3) return new GameFileSource(record.Identity, record.Size, null);
        if (await _identityResolver.StoreKeyAsync(_fileStore, record.Identity, record.Size) is { } key)
            return new GameFileSource(key, record.Size, null);

        if (!file.FileExists) return null;
        var (hashes, fingerprint) = await StableFileRead.Read(_fullHasher, file, cancellationToken);
        if (_fileHashesService.AttestedFiles(installation).TryGetValue(record.Path, out var attested))
            await _fileHashesService.SettleAttested(installation, [(attested, hashes, fingerprint)], cancellationToken);
        return new GameFileSource(FileIdentity.XxHash3(hashes.XxHash3), hashes.Size, file);
    }
}
