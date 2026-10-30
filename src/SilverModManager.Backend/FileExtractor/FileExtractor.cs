using Microsoft.Extensions.Logging;
using SilverModManager.Backend.FileExtractor.Extractors;
using SilverModManager.Backend.FileExtractor.FileSignatures;
using NexusMods.Paths;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// Utility used for extracting files.
/// This utility is usually created via DI container.
/// See <see cref="Services.AddFileExtractors"/>; and then ask the DI container nicely to give you an instance of this :)
/// </summary>
public class FileExtractor : IFileExtractor
{
    /// <summary>
    /// Used to check for file types by scanning file header signatures.
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public SignatureChecker SignatureChecker { get; }

    private readonly ILogger<FileExtractor> _logger;
    private readonly IEnumerable<IExtractor> _extractors;

    /// <summary>
    /// Creates a new instance of a File Extractor
    /// </summary>
    /// <param name="logger">Logger used to report back.</param>
    /// <param name="extractors">Extractors used to unpack files to disk.</param>
    /// <remarks>
    ///    This method is usually called via DI container.
    ///    See <see cref="Services.AddFileExtractors"/>; and then ask the DI container nicely to give you an instance of this.
    /// </remarks>
    public FileExtractor(ILogger<FileExtractor> logger, IEnumerable<IExtractor> extractors)
    {
        _logger = logger;
        _extractors = extractors.ToArray();
        SignatureChecker = new SignatureChecker(_extractors.SelectMany(e => e.SupportedSignatures).Distinct().ToArray());
    }

    /// <summary>
    /// Extracts all files to disk in an asynchronous fashion.
    /// </summary>
    /// <param name="path">The path to the source file.</param>
    /// <param name="dest">The folder where the file is to be extracted.</param>
    /// <param name="token">Used for cancellation of the operation.</param>
    public async Task ExtractAllAsync(AbsolutePath path, AbsolutePath dest, CancellationToken token = default)
    {
        await ExtractAllAsync(new NativeFileStreamFactory(path), dest, token);
    }

    /// <summary>
    /// Extracts all files to disk in an asynchronous fashion.
    /// </summary>
    /// <param name="sFn">The source stream.</param>
    /// <param name="dest">Destination file path.</param>
    /// <param name="token">Used for cancellation of the operation.</param>
    /// <exception cref="FileExtractionException"></exception>
    public async Task ExtractAllAsync(IStreamFactory sFn, AbsolutePath dest, CancellationToken token = default)
    {
        var extractors = await FindExtractors(sFn);
        var startedEmpty = IsEmpty(dest);
        Exception? lastFailure = null;
        foreach (var extractor in extractors)
        {
            try
            {
                await extractor.ExtractAllAsync(sFn, dest, token);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A name one extractor can't turn into a path (PathException) is no reason to give up: the next
                // extractor decodes names its own way, so it gets its turn like it does for any other failure.
                _logger.LogWarning(ex, "Extracting {File} via {Extractor} failed, trying the next extractor", sFn.FileName, extractor);
                lastFailure = ex;
                if (startedEmpty) Clear(dest);
            }
        }

        throw new FileExtractionException(lastFailure is null
            ? $"No Extractors found for file {sFn.FileName}"
            : $"No extractor could extract file {sFn.FileName}: {lastFailure.Message}", lastFailure);
    }

    private static bool IsEmpty(AbsolutePath folder)
    {
        var path = folder.ToString();
        return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
    }

    // Whatever a failed extractor wrote would otherwise end up beside the next extractor's output.
    private static void Clear(AbsolutePath folder)
    {
        var path = folder.ToString();
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToArray())
        {
            if (Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> CanExtract(AbsolutePath path)
    {
        return CanExtract(new NativeFileStreamFactory(path));
    }

    /// <inheritdoc/>
    public async ValueTask<bool> CanExtract(IStreamFactory sFn)
    {
        return await CanExtract(await sFn.GetStreamAsync());
    }

    /// <inheritdoc/>
    public async ValueTask<bool> CanExtract(Stream stream)
    {
        return await SignatureChecker.MatchesAnyAsync(stream);
    }

    private async ValueTask<IExtractor[]> FindExtractors(IStreamFactory sFn)
    {
        await using var archive = await sFn.GetStreamAsync();
        var sig = await SignatureChecker.MatchesAsync(archive);

        return _extractors.Select(e => (Extractor: e, Result: e.DeterminePriority(sig)))
            .Where(e => e.Result != Priority.None)
            .OrderBy(e => e.Result)
            .Select(e => e.Extractor)
            .ToArray();
    }
}
