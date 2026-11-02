using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

internal class AddLibraryFileJob : IJobDefinitionWithStart<AddLibraryFileJob, LibraryFile.New>, IAsyncDisposable
{
    public required ITransaction Transaction { get; init; }
    public required AbsolutePath FilePath { get; init; }
    public required IAcquisitionContext Acquisition { get; init; }
    private ConcurrentBag<TemporaryPath> ExtractionDirectories { get; } = [];
    private ConcurrentBag<ArchivedFileEntry> ToArchive { get; } = [];

    public static IJobTask<AddLibraryFileJob, LibraryFile.New> Create(IServiceProvider provider, ITransaction transaction, AbsolutePath filePath, IAcquisitionContext acquisition)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddLibraryFileJob
        {
            Transaction = transaction,
            FilePath = filePath,
            Acquisition = acquisition,
            FileExtractor = provider.GetRequiredService<IFileExtractor>(),
            Connection = provider.GetRequiredService<IConnection>(),
            TemporaryFileManager = provider.GetRequiredService<TemporaryFileManager>(),
            FileStore = provider.GetRequiredService<IFileStore>(),
            Recognisers = provider.GetServices<ILibraryFileRecogniser>().ToArray(),
            Logger = provider.GetRequiredService<ILogger<AddLibraryFileJob>>(),
        };
        return monitor.Begin<AddLibraryFileJob, LibraryFile.New>(job);
    }

    internal required ILogger<AddLibraryFileJob> Logger { get; init; }
    internal required IFileStore FileStore { get; set; }
    internal required ILibraryFileRecogniser[] Recognisers { get; init; }
    internal required TemporaryFileManager TemporaryFileManager { get; init; }
    internal required IFileExtractor FileExtractor { get; init; }
    public required IConnection Connection { get; set; }

    public async ValueTask<LibraryFile.New> StartAsync(IJobContext<AddLibraryFileJob> context)
    {
        if (!FilePath.FileExists)
            throw new Exception($"File '{FilePath}' does not exist.");

        var created = await AnalyzeFile(context, FilePath);
        var topFile = created ?? ReuseHeldCopy();
        if (created is not null) WriteDestination(created);
        else LibraryDestinations.Add(Transaction, topFile.Id, Acquisition.Destination(Connection.Db));
        await FileStore.BackupFiles(ToArchive, deduplicate: false, context.CancellationToken);
        return topFile;
    }

    private static readonly MultiHasherBuilder<Hash, XxHash3, Xx3Hasher, Md5Value, MD5, Md5Hasher, Sha256Value, SHA256, Sha256Hasher, Hash, XxHash64, Xx64Hasher> Hasher = MultiHasherBuilder.Start()
        .AddHasher<Hash, XxHash3, Xx3Hasher>()
        .AddHasher<Md5Value, MD5, Md5Hasher>()
        .AddHasher<Sha256Value, SHA256, Sha256Hasher>()
        .AddHasher<Hash, XxHash64, Xx64Hasher>();

    // Nested members are hashed with the two xxHash variants only, in one pass.
    private static readonly MultiHasherBuilder<Hash, XxHash3, Xx3Hasher, Hash, XxHash64, Xx64Hasher> NestedHasher = MultiHasherBuilder.Start()
        .AddHasher<Hash, XxHash3, Xx3Hasher>()
        .AddHasher<Hash, XxHash64, Xx64Hasher>();

    private static async ValueTask<(Hash, Optional<Md5Value>, Optional<Sha256Value>, Hash)> HashAsync(AbsolutePath filePath, bool isNestedFile, CancellationToken cancellationToken)
    {
        return await FileHashing.Read(filePath, async (stream, token) =>
        {
            if (isNestedFile)
            {
                var nested = await NestedHasher.HashAsync(stream, cancellationToken: token).ConfigureAwait(false);
                return (nested.Hash1, Optional<Md5Value>.None, Optional<Sha256Value>.None, nested.Hash2);
            }

            var multiHash = await Hasher.HashAsync(stream, cancellationToken: token);
            return (multiHash.Hash1, multiHash.Hash2, multiHash.Hash3, multiHash.Hash4);
        }, cancellationToken);
    }

    /// <summary>
    /// The top-level library file this job found already holding the same bytes. Its rows stay as they are, and
    /// only the bytes the file store lacks (a nested archive an older import did not keep) are added.
    /// </summary>
    private LibraryFile.ReadOnly? _heldCopy;
    private Hash _heldHash;
    private Optional<Md5Value> _heldMd5;
    private Optional<Sha256Value> _heldSha256;
    private Hash _heldXxHash64;

    /// <summary>
    /// Adds the file's rows, or nothing when the library already holds this top-level file (the result is then
    /// null and <see cref="ReuseHeldCopy"/> names the held one). Either way the bytes the store lacks are queued.
    /// </summary>
    private async Task<LibraryFile.New?> AnalyzeFile(IJobContext<AddLibraryFileJob> context, AbsolutePath filePath, bool isNestedFile = false, bool recordRows = true)
    {
        var isArchive = isNestedFile ? await CheckIfNestedArchiveAsync(filePath) : await CheckIfArchiveAsync(filePath);

        var (hash, md5, sha256, xxHash64) = await HashAsync(filePath, isNestedFile: isNestedFile, cancellationToken: context.CancellationToken);
        if (recordRows && !isNestedFile && FindHeldCopy(hash) is { } held)
        {
            (_heldCopy, _heldHash, _heldMd5, _heldSha256, _heldXxHash64) = (held, hash, md5, sha256, xxHash64);
            recordRows = false;
        }

        var libraryFile = recordRows ? CreateLibraryFile(Transaction, filePath, hash, md5, sha256, xxHash64) : null;

        AbsolutePath? extractedFolder = null;
        if (isArchive)
        {
            var extractionFolder = TemporaryFileManager.CreateFolder();
            // Add the temp folder for later
            ExtractionDirectories.Add(extractionFolder);
            try
            {
                await FileExtractor.ExtractAllAsync(filePath, extractionFolder, token: context.CancellationToken);
                extractedFolder = extractionFolder.Path;
            }
            catch (Exception e) when (isNestedFile && !context.CancellationToken.IsCancellationRequested)
            {
                // A nested archive is only a bonus listing of what is inside the download. Failing to open one
                // must not fail the download, so it is kept as the plain file it is.
                Logger.LogWarning(e, "Could not extract nested archive `{File}` from `{Outer}`, adding it as a plain file", filePath.FileName, FilePath.FileName);
            }
        }

        if (extractedFolder is { } folder)
        {
            var libraryArchive = libraryFile is null
                ? null
                : new LibraryArchive.New(Transaction, libraryFile.Id)
                {
                    IsArchive = true,
                    LibraryFile = libraryFile,
                };

            foreach (var extracted in folder.EnumerateFiles())
            {
                var subFile = await AnalyzeFile(context, extracted, isNestedFile: true, recordRows: recordRows);
                if (libraryArchive is null || subFile is null) continue;
                var path = extracted.RelativeTo(folder);
                _ = new LibraryArchiveFileEntry.New(Transaction, subFile.Id)
                {
                    Path = path,
                    LibraryFile = subFile,
                    ParentId = libraryArchive.Id,
                };
            }

            // Only the members of an expanded archive are kept, so a nested one would otherwise lose its own bytes
            // and nothing could copy it whole (a Wabbajack directive can). The outer download is not kept either,
            // so they cannot be recovered from it later.
            if (isNestedFile) await ArchiveBytes(filePath, hash);

            // The extraction folder is only around until the job is disposed, so recognisers see it now.
            if (!isNestedFile && libraryFile is not null) await RecogniseAsync(libraryFile, folder, context.CancellationToken);
        }
        else
        {
            await ArchiveBytes(filePath, hash);

            if (!isNestedFile && libraryFile is not null) await RecogniseAsync(libraryFile, extractedFolder: null, context.CancellationToken);
        }

        return libraryFile;
    }

    /// <summary>
    /// A top-level library file with these bytes, if the library has one. A second row for a re-fetched archive
    /// would show the same file twice and leave two rows for one set of nested entries.
    /// </summary>
    private LibraryFile.ReadOnly? FindHeldCopy(Hash hash)
    {
        var db = Connection.Db;
        foreach (var datom in db.Datoms(LibraryFile.Hash, hash))
        {
            var file = LibraryFile.Load(db, datom.E);
            if (file.IsValid() && !LibraryArchiveFileEntry.IsMember(db, file.Id)) return file;
        }
        return null;
    }

    /// <summary>
    /// The held copy as the job's result, so callers attach their metadata to it. It carries the held file's own
    /// name and the values it was added with, so committing it changes nothing on the row.
    /// </summary>
    private LibraryFile.New ReuseHeldCopy()
    {
        var held = _heldCopy ?? throw new InvalidOperationException("No held copy was found for this file.");
        return CreateLibraryFile(Transaction, held.FileName, held.AsLibraryItem().Name, FilePath.FileInfo.Size, _heldHash, _heldMd5, _heldSha256, _heldXxHash64, reuse: held.Id);
    }

    private async Task ArchiveBytes(AbsolutePath filePath, Hash hash)
    {
        var identity = FileIdentity.XxHash3(hash);
        if (!await FileStore.HaveFile(identity))
            ToArchive.Add(new ArchivedFileEntry(new NativeFileStreamFactory(filePath), identity, filePath.FileInfo.Size));
    }

    /// <summary>
    /// The first destination a recogniser names for the top-level file, if any. The recognisers run inside the
    /// transaction, and a second assert on the same entity would leave both values in the database, so the
    /// destination is written once, after them, from this answer or the acquisition's.
    /// </summary>
    private Destination? _recognised;

    private async ValueTask RecogniseAsync(LibraryFile.New libraryFile, AbsolutePath? extractedFolder, CancellationToken cancellationToken)
    {
        foreach (var recogniser in Recognisers)
        {
            var named = await recogniser.Recognise(Transaction, libraryFile, extractedFolder, cancellationToken);
            _recognised ??= named;
        }
    }

    private void WriteDestination(LibraryFile.New topFile)
        => LibraryDestinations.Write(Transaction, topFile.Id, _recognised ?? Acquisition.Destination(Connection.Db));

    /// <summary>
    /// Returns true if the file can be considered an archive and extracted.
    /// </summary>
    private async Task<bool> CheckIfArchiveAsync(AbsolutePath filePath)
    {
        await using var stream = filePath.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        var canExtract = await FileExtractor.CanExtract(stream);
        return canExtract;
    }

    /// <summary>
    /// Returns true if a file that is nested inside an archive can/should be extracted.
    /// This check is more restrictive than just <see cref="CheckIfArchiveAsync"/>, with a filter of supported extensions.
    /// Some games support archive formats as valid mod files, those should not be extracted but treated as a single file instead.
    /// </summary>
    private async Task<bool> CheckIfNestedArchiveAsync(AbsolutePath filePath)
    {
        if (NestedArchives.MayBeExpanded(filePath))
            return await CheckIfArchiveAsync(filePath);
        return false;
    }

    private static LibraryFile.New CreateLibraryFile(ITransaction tx, AbsolutePath filePath, Hash hash, Optional<Md5Value> md5, Optional<Sha256Value> sha256, Hash xxHash64)
        => CreateLibraryFile(tx, filePath.FileName, filePath.FileName, filePath.FileInfo.Size, hash, md5, sha256, xxHash64, reuse: null);

    private static LibraryFile.New CreateLibraryFile(ITransaction tx, RelativePath fileName, string itemName, Size size, Hash hash, Optional<Md5Value> md5, Optional<Sha256Value> sha256, Hash xxHash64, EntityId? reuse)
    {
        var id = reuse ?? tx.TempId();
        var libraryFile = new LibraryFile.New(tx, id)
        {
            FileName = fileName,
            Hash = hash,
            Md5 = md5.ValueOrDefault(),
            XxHash64 = xxHash64,
            Size = size,
            LibraryItem = new LibraryItem.New(tx, id)
            {
                Name = itemName,
            },
        };

        // Only top-level files carry a digest; an unset value must not be written as an all-zero one.
        if (sha256.HasValue) tx.Add(libraryFile.Id, LibraryFile.Sha256, sha256.Value);

        return libraryFile;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var directory in ExtractionDirectories)
        {
            await directory.DisposeAsync();
        }

        ExtractionDirectories.Clear();
    }
}
