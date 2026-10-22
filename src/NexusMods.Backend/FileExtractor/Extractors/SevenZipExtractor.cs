using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Disposables;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.FileExtractor;
using NexusMods.FileExtractor;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk;
using NexusMods.Sdk.IO;
using Reloaded.Memory.Extensions;

namespace NexusMods.Backend.FileExtractor.Extractors;

/// <summary>
/// Abstracts the 7-zip archive extractor.
/// </summary>
/// <remarks>
///     Uses the 7z binary for each native platform under the hood.
///     We chose this tradeoff due to a lack of decent cross platform option and
///     reportedly possible AVs on invalid archives.
/// </remarks>
public class SevenZipExtractor : IExtractor
{
    private static readonly FileType[] SupportedTypesCached = [FileType._7Z, FileType.RAR_NEW, FileType.RAR_OLD, FileType.ZIP, FileType.GZ];
    private static readonly Extension[] SupportedExtensionsCached = [KnownExtensions._7z, KnownExtensions.Rar, KnownExtensions.Zip, KnownExtensions._7zip, new Extension(".gz"), new Extension(".tgz")];

    /// <inheritdoc />
    public FileType[] SupportedSignatures => SupportedTypesCached;

    /// <inheritdoc />
    public Extension[] SupportedExtensions => SupportedExtensionsCached;

    private readonly ILogger _logger;
    private readonly IOSInformation _osInformation;
    private readonly IProcessRunner _processRunner;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly string _exePath;

    /// <summary>
    /// Constructor.
    /// </summary>
    public SevenZipExtractor(
        ILogger<SevenZipExtractor> logger,
        TemporaryFileManager fileTemporaryFileManager,
        IFileSystem fileSystem, 
        IProcessRunner processRunner,
        IOSInformation osInformation)
    {
        _logger = logger;
        _temporaryFileManager = fileTemporaryFileManager;
        _processRunner = processRunner;
        _osInformation = osInformation;

        _exePath = GetExtractorExecutable(fileSystem);
        logger.LogDebug("Using extractor at {ExtractorExecutable}", _exePath);
    }

    /// <inheritdoc />
    public async Task ExtractAllAsync(IStreamFactory streamFactory, AbsolutePath destination, CancellationToken cancellationToken)
    {
        var (source, sourceDisposable) = await GetSource(streamFactory, cancellationToken: cancellationToken);
        using var _ = sourceDisposable;

        var trimmablePathsInArchive = await GetTrimmablePathsInArchive(source, cancellationToken: cancellationToken);
        await ExtractArchive(source, streamFactory.FileName.ToString(), destination, cancellationToken: cancellationToken);

        using var innerTar = await TakeInnerTar(source, destination, cancellationToken);
        if (innerTar is not null)
        {
            trimmablePathsInArchive = await GetTrimmablePathsInArchive(innerTar.Value.Path, cancellationToken: cancellationToken);
            await ExtractArchive(innerTar.Value.Path, streamFactory.FileName.ToString(), destination, cancellationToken: cancellationToken);
        }

        // Modes first, since the link sweep has to list every folder and copy link targets that the archive
        // may have stored at 000; NormalizeModes never touches a link. Then the links go, before anything
        // else reads the result.
        var root = destination.ToNativeSeparators(_osInformation);
        NormalizeModes(root);
        ExtractedLinks.Sanitize(root, streamFactory.FileName.ToString(), _logger);

        if (trimmablePathsInArchive.Count == 0) return;
        FixPaths(trimmablePathsInArchive, destination);
    }

    /// <summary>
    /// 7z strips one layer at a time, so a .tar.gz comes out as a single tar file and not its members. When a
    /// gzip source produced nothing but a tar, the tar is moved out of the destination and returned for
    /// extracting in its place. It is recognised by its header, not its name: 7z names it after the archive,
    /// and a download saved without the .tar.gz suffix gives a tar with some other name.
    /// </summary>
    private async ValueTask<TemporaryPath?> TakeInnerTar(AbsolutePath source, AbsolutePath destination, CancellationToken cancellationToken)
    {
        if (!await HasBytesAt(source, offset: 0, GzipMagic, cancellationToken)) return null;

        var entries = Directory.GetFileSystemEntries(destination.ToNativeSeparators(_osInformation));
        if (entries.Length != 1 || !File.Exists(entries[0])) return null;

        var tar = destination.Combine(Path.GetFileName(entries[0]));
        if (!await HasBytesAt(tar, TarMagicOffset, TarMagic, cancellationToken)) return null;

        var moved = _temporaryFileManager.CreateFile(ext: new Extension(".tar"));
        File.Move(entries[0], moved.Path.ToNativeSeparators(_osInformation), overwrite: true);
        return moved;
    }

    private static readonly byte[] GzipMagic = [0x1F, 0x8B];

    // POSIX and GNU tar headers both carry "ustar" at this offset.
    private const int TarMagicOffset = 257;
    private static readonly byte[] TarMagic = "ustar"u8.ToArray();

    private static async ValueTask<bool> HasBytesAt(AbsolutePath path, int offset, byte[] expected, CancellationToken cancellationToken)
    {
        await using var stream = path.Read();
        var buffer = new byte[offset + expected.Length];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return read == buffer.Length && buffer.AsSpan(offset).SequenceEqual(expected);
    }

    private const UnixFileMode RegularFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Executable = RegularFile | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// 7z restores the Unix mode stored in the archive, and what archives store is unreliable: some
    /// store 000, which left extracted files the app couldn't read back to hash. The stored mode is
    /// ignored apart from the owner execute bit: files become 0644, or 0755 when the archive marked
    /// them executable (Linux-native tools and scripts need it), and directories become 0755.
    /// </summary>
    internal static void NormalizeModes(string root)
    {
        if (!Directory.Exists(root)) return;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            // A directory has to be fixed before it can be listed.
            File.SetUnixFileMode(directory, Executable);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // An archive can carry symlinks and chmod follows them, so a link is never touched:
                // its target may be outside the extraction folder.
                if (new FileInfo(file).LinkTarget is not null) continue;
                var executable = File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserExecute);
                File.SetUnixFileMode(file, executable ? Executable : RegularFile);
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (new DirectoryInfo(child).LinkTarget is not null) continue;
                pending.Push(child);
            }
        }
    }

    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    private void FixPaths(
        IReadOnlyList<(string fileName, bool isDirectory)> trimmablePathsInArchive,
        AbsolutePath destinationPath)
    {
        // NOTE(erri120): We need to fix paths that end in whitespace as they cause a lot of issues.
        // 7z extracts "foo/bar " to "foo/bar " properly, but our path sanitization will trim the path and the app won't find the file on disk

        var nativePath = destinationPath.ToNativeSeparators(_osInformation);

        foreach (var tuple in trimmablePathsInArchive)
        {
            var (fileName, isDirectory) = tuple;

            var fixedFileName = PathsHelper.FixFileName(fileName);
            Debug.Assert(fixedFileName.All(c => !PathsHelper.IsInvalidChar(c)), message: $"`{fixedFileName}` should be fixed");

            var source = System.IO.Path.Combine(nativePath, fileName);
            var destination = System.IO.Path.Combine(nativePath, fixedFileName);

            if (isDirectory)
            {
                try
                {
                    _logger.LogWarning("Fixing path by moving directory from `{From}` to `{To}`", source, destination);
                    if (System.IO.Directory.Exists(destination))
                    {
                        _logger.LogWarning("Destination directory `{Destination}` already exists, deleting source instead", destination);
                        System.IO.Directory.Delete(source, recursive: true);
                    }
                    else
                    {
                        System.IO.Directory.Move(source, destination);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to fix path by moving directory from `{From}` to `{To}`", source, destination);
                }
            }
            else
            {
                try
                {
                    _logger.LogWarning("Fixing path by moving file from `{From}` to `{To}`", source, destination);
                    if (System.IO.File.Exists(destination))
                    {
                        _logger.LogWarning("Destination file `{Destination}` already exists, deleting source instead", destination);
                        System.IO.File.Delete(source);
                    }
                    else
                    {
                        System.IO.File.Move(source, destination);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to fix path by moving file from `{From}` to `{To}`", source, destination);
                }
            }
        }
    }

    /// <summary>
    /// Returns a list of all paths in the archive that need to be trimmed.
    /// </summary>
    private async ValueTask<IReadOnlyList<(string fileName, bool isDirectory)>> GetTrimmablePathsInArchive(AbsolutePath source, CancellationToken cancellationToken)
    {
        var pathsWithInvalidCharacters = new List<(string fileName, bool isDirectory)>();

        // NOTE(erri120): "l -ba" is an undocumented command that skips the table header and footer
        var process = Cli
            .Wrap(_exePath)
            .WithArguments(["l", "-ba", $"{source.ToNativeSeparators(_osInformation)}"])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                if (!TryParseListCommandOutput(line, out var fileName, out var isDirectory)) return;
                pathsWithInvalidCharacters.Add((fileName, isDirectory));
            }))
            .WithValidation(CommandResultValidation.ZeroExitCode);

        await _processRunner.RunAsync(process, logOutput: true, cancellationToken: cancellationToken);
        return pathsWithInvalidCharacters;
    }

    /// <summary>
    /// Parses a table row returned by the 7z list command.
    /// </summary>
    internal static bool TryParseListCommandOutput(ReadOnlySpan<char> line, [NotNullWhen(true)] out string? fileName, out bool isDirectory)
    {
        // The table that gets printed has a fixed minimum width:
        // https://github.com/btolab/p7zip/blob/f30c859433af90937723dd4e808a24c3bb836711/CPP/7zip/UI/Console/List.cpp#L186-L193
        const int fixedLengthBeforeAttributes = 20;
        const int fixedLengthBeforeFileName = 53;

        fileName = null;
        isDirectory = false;

        if (line.Length < fixedLengthBeforeFileName) return false;

        var attributesSlice = line.SliceFast(start: fixedLengthBeforeAttributes);
        isDirectory = attributesSlice[0] == 'D';

        var fileNameSlice = line.SliceFast(start: fixedLengthBeforeFileName);

        // NOTE(erri120): "." and ".." can for some reason end up in the archive
        // 7z doesn't extract them so we should just ignore them
        if (fileNameSlice.Length == 1 && fileNameSlice[0] == '.') return false;
        if (fileNameSlice.Length == 2 && fileNameSlice[0] == '.' && fileNameSlice[1] == '.') return false;

        var lastChar = fileNameSlice[^1];
        if (!PathsHelper.IsInvalidChar(lastChar)) return false;

        fileName = fileNameSlice.ToString();
        return true;
    }

    private async ValueTask<(AbsolutePath, IDisposable)> GetSource(IStreamFactory streamFactory, CancellationToken cancellationToken)
    {
        if (streamFactory is NativeFileStreamFactory nativeFileStreamFactory) return (nativeFileStreamFactory.Path, Disposable.Empty);

        var temporaryFile = _temporaryFileManager.CreateFile(ext: streamFactory.FileName.Extension);
        await using var stream = await streamFactory.GetStreamAsync();
        await temporaryFile.Path.CopyFromAsync(stream, cancellationToken);

        return (temporaryFile.Path, temporaryFile);
    }

    // 7z refuses to create a link whose target contains "..", and reports each one on stderr in one of
    // these forms while exiting with code 2; the second is used for a target that climbs and comes back
    // down inside the archive. There is no switch that stops it creating the remaining links (-snld only
    // allows the dangerous ones), so ExtractedLinks sweeps the result.
    private static readonly string[] DangerousLinkErrors = ["ERROR: Dangerous link path was ignored", "ERROR: Dangerous symbolic link path was ignored"];

    /// <summary>
    /// Extracts with 7z, then makes the result match <see cref="ArchiveEntryPath.Clean"/>. Only entries whose
    /// names 7z would place differently are handled separately, so an archive with well-formed names is a
    /// single 7z run.
    /// </summary>
    private async ValueTask ExtractArchive(AbsolutePath source, string archiveName, AbsolutePath destination, CancellationToken cancellationToken)
    {
        var plan = new ArchiveEntryPlan(await ListEntries(source, cancellationToken));
        if (plan.IsPlain)
        {
            await RunExtract(source, archiveName, destination, extraArguments: [], cancellationToken);
            plan.Rewrites.Log(_logger, archiveName);
            return;
        }

        // 7z reads the list as UTF-8 and, with -spd, takes each line as a literal name and not a pattern.
        await using var excludeList = _temporaryFileManager.CreateFile(ext: new Extension(".txt"));
        await File.WriteAllLinesAsync(excludeList.Path.ToNativeSeparators(_osInformation), plan.Excluded, new UTF8Encoding(false), cancellationToken);
        await RunExtract(source, archiveName, destination, ["-spd", "-scsUTF-8", $"-x@{excludeList.Path.ToNativeSeparators(_osInformation)}"], cancellationToken);

        var root = destination.ToNativeSeparators(_osInformation);
        foreach (var folder in plan.Folders) Directory.CreateDirectory(Path.Combine(root, folder));

        foreach (var (name, cleaned) in plan.Moved)
        {
            await using var scratch = _temporaryFileManager.CreateFolder();
            await RunExtract(source, archiveName, scratch.Path, ["-spd", $"-i!{name}"], cancellationToken);

            var extracted = FindExtractedEntries(scratch.Path.ToNativeSeparators(_osInformation));
            if (extracted.Count == 0)
            {
                _logger.LogWarning("7z produced nothing for entry `{Entry}` of archive `{Archive}`", name, archiveName);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, cleaned));
            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new FileExtractionException($"Cleaned path `{cleaned}` of entry `{name}` in `{archiveName}` is outside the extraction folder");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException($"`{target}` has no parent folder"));
            File.Move(extracted[0], target, overwrite: true);
        }

        plan.Rewrites.Log(_logger, archiveName);
    }

    /// <summary>
    /// Everything 7z extracted into a folder that held no other entry, links included and never followed.
    /// </summary>
    private static List<string> FindExtractedEntries(string folder)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (info is DirectoryInfo && info.LinkTarget is null) pending.Push(info.FullName);
                else result.Add(info.FullName);
            }
        }

        return result;
    }

    /// <summary>
    /// Lists the entries with their names as stored. <c>-slt</c> prints one "Path = " block per entry, and
    /// the plain table would hide a leading "../" behind column alignment.
    /// </summary>
    private async ValueTask<List<ArchiveEntryPlan.Entry>> ListEntries(AbsolutePath source, CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveEntryPlan.Entry>();
        string? name = null;
        var isDirectory = false;

        void Flush()
        {
            if (name is not null) entries.Add(new ArchiveEntryPlan.Entry(name, isDirectory));
            name = null;
            isDirectory = false;
        }

        var process = Cli
            .Wrap(_exePath)
            .WithArguments(["l", "-slt", "-ba", "-sccUTF-8", $"{source.ToNativeSeparators(_osInformation)}"])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                if (line.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    Flush();
                    name = line["Path = ".Length..];
                }
                else if (line.StartsWith("Folder = ", StringComparison.Ordinal))
                {
                    isDirectory = line["Folder = ".Length..] == "+";
                }
            }))
            .WithValidation(CommandResultValidation.ZeroExitCode);

        await _processRunner.RunAsync(process, logOutput: false, cancellationToken: cancellationToken);
        Flush();
        return entries;
    }

    private async ValueTask RunExtract(AbsolutePath source, string archiveName, AbsolutePath destination, string[] extraArguments, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // NOTE(erri120): Using "-bsp1" to redirect the progress line to stdout
        var process = Cli
            .Wrap(_exePath)
            .WithArguments(["x", "-bsp1", "-y", .. extraArguments, $"-o{destination.ToNativeSeparators(_osInformation)}", $"{source.ToNativeSeparators(_osInformation)}"])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                // TODO: progress reporting
                _ = TryParseExtractCommandOutput(line, out var percentage);
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) errors.Add(line);
            }))
            .WithValidation(CommandResultValidation.None);

        var result = await _processRunner.RunAsync(process, logOutput: true, cancellationToken: cancellationToken);
        if (result.ExitCode == 0) return;

        // An archive whose only problem is links it was not allowed to create still extracts: the links are
        // dropped and the rest of the mod is usable.
        if (errors.Count > 0 && errors.All(e => DangerousLinkErrors.Any(prefix => e.StartsWith(prefix, StringComparison.Ordinal))))
        {
            foreach (var error in errors) _logger.LogWarning("Dropped a symlink from `{Archive}`: {Error}", archiveName, error);
            return;
        }

        throw new FileExtractionException($"7z exited with code {result.ExitCode} extracting `{archiveName}`: {string.Join(" | ", errors)}");
    }

    private static bool TryParseExtractCommandOutput(ReadOnlySpan<char> line, out int percentage)
    {
        // "  0%"
        // " 10%"
        // "100%"
        const int percentageIndex = 3;
        percentage = 0;

        if (line.Length < percentageIndex + 1) return false;
        if (line[percentageIndex] != '%') return false;

        var rawPercentage = line.SliceFast(start: 0, length: percentageIndex);
        return int.TryParse(rawPercentage, style: NumberStyles.None, provider: NumberFormatInfo.InvariantInfo, out percentage);
    }

    /// <inheritdoc />
    public Priority DeterminePriority(IEnumerable<FileType> signatures)
    {
        // Yes this is O(n*m) but the search space (should) be very small.
        // 'signatures' should usually be only 1 element :)
        if ((from supported in SupportedSignatures
                from sig in signatures
                where supported == sig
                select supported).Any())
        {
            return Priority.Low;
        }

        return Priority.None;
    }

    private static string GetExtractorExecutable(IFileSystem fileSystem)
    {
#pragma warning disable CS0162 // Unreachable code detected
        if (UseSystemExtractor)
        {
            // Depending on the user's distro and package of choice, 7z
            // may have different names, so we'll check for the common ones.
            return FindSystem7zOnLinux();
        }

        return fileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("runtimes/linux-x64/native/7zz").ToString();
#pragma warning restore CS0162 // Unreachable code detected
    }

    /// <summary>
    /// Useful for Linux packages that already install 7z globally.
    /// </summary>
    /// <remarks>
    /// See https://github.com/Nexus-Mods/NexusMods.App/issues/1306 for details.
    /// </remarks>
    private const bool UseSystemExtractor =
#if NEXUSMODS_APP_USE_SYSTEM_EXTRACTOR
        true;
#else
        false;
#endif

    private static string FindSystem7zOnLinux()
    {
        string[] potentialBinaryNames = ["7z", "7zz", "7zzs"];
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? throw new InvalidOperationException("PATH is not set, cannot look for the system 7z binary")).Split(':');

        foreach (var pathDir in pathDirectories)
        foreach (var binaryName in potentialBinaryNames)
        {
            if (File.Exists(Path.Combine(pathDir, binaryName)))
                return binaryName;
        }

        throw new Exception("Cannot find system 7z binary in PATH");
    }
}
