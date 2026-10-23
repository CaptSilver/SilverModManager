using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;

namespace NexusMods.Library;

internal class AddLibraryFileJob : IJobDefinitionWithStart<AddLibraryFileJob, LibraryFile.New>, IAsyncDisposable
{
    public required ITransaction Transaction { get; init; }
    public required AbsolutePath FilePath { get; init; }
    private ConcurrentBag<TemporaryPath> ExtractionDirectories { get; } = [];
    private ConcurrentBag<ArchivedFileEntry> ToArchive { get; } = [];

    private Extension[] NestedArchiveExtensions { get; } =
    [
        KnownExtensions._7z, KnownExtensions.Rar, KnownExtensions.Zip, KnownExtensions._7zip,
        // Stardew Valley SMAPI nested archive
        new(".dat"),
    ];

    public static IJobTask<AddLibraryFileJob, LibraryFile.New> Create(IServiceProvider provider, ITransaction transaction, AbsolutePath filePath)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddLibraryFileJob
        {
            Transaction = transaction,
            FilePath = filePath,
            FileExtractor = provider.GetRequiredService<IFileExtractor>(),
            Connection = provider.GetRequiredService<IConnection>(),
            TemporaryFileManager = provider.GetRequiredService<TemporaryFileManager>(),
            FileStore = provider.GetRequiredService<IFileStore>(),
            Recognisers = provider.GetServices<ILibraryFileRecogniser>().ToArray(),
        };
        return monitor.Begin<AddLibraryFileJob, LibraryFile.New>(job);
    }

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
        await using var stream = filePath.Read();

        if (isNestedFile)
        {
            var nested = await NestedHasher.HashAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (nested.Hash1, Optional<Md5Value>.None, Optional<Sha256Value>.None, nested.Hash2);
        }

        var multiHash = await Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        return (multiHash.Hash1, multiHash.Hash2, multiHash.Hash3, multiHash.Hash4);
    }

    private async Task<LibraryFile.New> AnalyzeFile(IJobContext<AddLibraryFileJob> context, AbsolutePath filePath, bool isNestedFile = false)
    {
        var isArchive = isNestedFile ? await CheckIfNestedArchiveAsync(filePath) : await CheckIfArchiveAsync(filePath);

        var (hash, md5, sha256, xxHash64) = await HashAsync(filePath, isNestedFile: isNestedFile, cancellationToken: context.CancellationToken);
        var libraryFile = CreateLibraryFile(Transaction, filePath, hash, md5, sha256, xxHash64);

        if (isArchive)
        {
            var libraryArchive = new LibraryArchive.New(Transaction, libraryFile.Id)
            {
                IsArchive = true,
                LibraryFile = libraryFile,
            };

            var extractionFolder = TemporaryFileManager.CreateFolder();
            // Add the temp folder for later
            ExtractionDirectories.Add(extractionFolder);
            await FileExtractor.ExtractAllAsync(filePath, extractionFolder, token: context.CancellationToken);

            var extractedFiles = extractionFolder.Path.EnumerateFiles();

            foreach (var extracted in extractedFiles)
            {
                var subFile = await AnalyzeFile(context, extracted, isNestedFile: true);
                var path = extracted.RelativeTo(extractionFolder.Path);
                _ = new LibraryArchiveFileEntry.New(Transaction, subFile.Id)
                {
                    Path = path,
                    LibraryFile = subFile,
                    ParentId = libraryArchive.Id,
                };
            }

            // The extraction folder is only around until the job is disposed, so recognisers see it now.
            if (!isNestedFile) await RecogniseAsync(libraryFile, extractionFolder.Path, context.CancellationToken);
        }
        else
        {
            var size = filePath.FileInfo.Size;
            if (!await FileStore.HaveFile(hash))
                ToArchive.Add(new ArchivedFileEntry(new NativeFileStreamFactory(filePath), hash, size));

            if (!isNestedFile) await RecogniseAsync(libraryFile, extractedFolder: null, context.CancellationToken);
        }

        return libraryFile;
    }

    private async ValueTask RecogniseAsync(LibraryFile.New libraryFile, AbsolutePath? extractedFolder, CancellationToken cancellationToken)
    {
        foreach (var recogniser in Recognisers)
        {
            await recogniser.Recognise(Transaction, libraryFile, extractedFolder, cancellationToken);
        }
    }

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
