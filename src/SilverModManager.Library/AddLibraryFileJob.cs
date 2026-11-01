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

    private Extension[] NestedArchiveExtensions { get; } =
    [
        KnownExtensions._7z, KnownExtensions.Rar, KnownExtensions.Zip, KnownExtensions._7zip,
        // Stardew Valley SMAPI nested archive
        new(".dat"),
    ];

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

        var topFile = await AnalyzeFile(context, FilePath);
        WriteDestination(topFile);
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

    private async Task<LibraryFile.New> AnalyzeFile(IJobContext<AddLibraryFileJob> context, AbsolutePath filePath, bool isNestedFile = false)
    {
        var isArchive = isNestedFile ? await CheckIfNestedArchiveAsync(filePath) : await CheckIfArchiveAsync(filePath);

        var (hash, md5, sha256, xxHash64) = await HashAsync(filePath, isNestedFile: isNestedFile, cancellationToken: context.CancellationToken);
        var libraryFile = CreateLibraryFile(Transaction, filePath, hash, md5, sha256, xxHash64);

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
            var libraryArchive = new LibraryArchive.New(Transaction, libraryFile.Id)
            {
                IsArchive = true,
                LibraryFile = libraryFile,
            };

            foreach (var extracted in folder.EnumerateFiles())
            {
                var subFile = await AnalyzeFile(context, extracted, isNestedFile: true);
                var path = extracted.RelativeTo(folder);
                _ = new LibraryArchiveFileEntry.New(Transaction, subFile.Id)
                {
                    Path = path,
                    LibraryFile = subFile,
                    ParentId = libraryArchive.Id,
                };
            }

            // The extraction folder is only around until the job is disposed, so recognisers see it now.
            if (!isNestedFile) await RecogniseAsync(libraryFile, folder, context.CancellationToken);
        }
        else
        {
            var size = filePath.FileInfo.Size;
            var identity = FileIdentity.XxHash3(hash);
            if (!await FileStore.HaveFile(identity))
                ToArchive.Add(new ArchivedFileEntry(new NativeFileStreamFactory(filePath), identity, size));

            if (!isNestedFile) await RecogniseAsync(libraryFile, extractedFolder: null, context.CancellationToken);
        }

        return libraryFile;
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
        if (NestedArchiveExtensions.Contains(filePath.Extension))
            return await CheckIfArchiveAsync(filePath);
        return false;
    }

    private static LibraryFile.New CreateLibraryFile(ITransaction tx, AbsolutePath filePath, Hash hash, Optional<Md5Value> md5, Optional<Sha256Value> sha256, Hash xxHash64)
    {
        var libraryFile = new LibraryFile.New(tx, out var id)
        {
            FileName = filePath.FileName,
            Hash = hash,
            Md5 = md5.ValueOrDefault(),
            XxHash64 = xxHash64,
            Size = filePath.FileInfo.Size,
            LibraryItem = new LibraryItem.New(tx, id)
            {
                Name = filePath.FileName,
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
