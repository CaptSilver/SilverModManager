using System.Globalization;
using System.Reactive.Disposables;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.FileExtractor;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk;
using SilverModManager.Sdk.IO;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Backend.FileExtractor.Extractors;

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

        await ExtractArchive(source, streamFactory.FileName.ToString(), destination, cancellationToken: cancellationToken);

        using var innerTar = await TakeInnerTar(source, destination, cancellationToken);
        if (innerTar is not null)
        {
            await ExtractArchive(innerTar.Value.Path, streamFactory.FileName.ToString(), destination, cancellationToken: cancellationToken);
        }

        // Links are swept inside ExtractArchive, before any entry is written by hand; this only settles the
        // modes of what the last run left. NormalizeModes never touches a link.
        NormalizeModes(destination.ToNativeSeparators(_osInformation));
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
        if (entries.Length != 1 || SafeExtraction.IsLink(entries[0]) || !File.Exists(entries[0])) return null;

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

    private async ValueTask<(AbsolutePath, IDisposable)> GetSource(IStreamFactory streamFactory, CancellationToken cancellationToken)
    {
        if (streamFactory is NativeFileStreamFactory nativeFileStreamFactory) return (nativeFileStreamFactory.Path, Disposable.Empty);

        var temporaryFile = _temporaryFileManager.CreateFile(ext: streamFactory.FileName.Extension);
        await using var stream = await streamFactory.GetStreamAsync();
        await temporaryFile.Path.CopyFromAsync(stream, cancellationToken);

        return (temporaryFile.Path, temporaryFile);
    }

    // 7z refuses to create a link whose target contains "..", or runs through another link of the archive,
    // and reports each one on stderr in one of these forms while exiting with code 2. 26.05 prints the first
    // and the second; the third is what 21.03 printed, kept for a system 7z (SMM_USE_SYSTEM_EXTRACTOR).
    // There is no switch that stops it creating the remaining links (-snld only allows the dangerous ones),
    // so ExtractedLinks sweeps the result.
    private static readonly string[] DangerousLinkErrors =
    [
        "ERROR: Dangerous link path was ignored",
        "ERROR: Dangerous link via another link was ignored",
        "ERROR: Dangerous symbolic link path was ignored",
    ];

    /// <summary>True when 7z reported errors and every one of them is a link it refused to create.</summary>
    internal static bool AreAllDangerousLinkErrors(IReadOnlyCollection<string> errors) =>
        errors.Count > 0 && errors.All(e => DangerousLinkErrors.Any(prefix => e.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>
    /// A 7z command. The locale is pinned so that what 7z prints and how it reads a list file never depend on
    /// the user's LANG; the probed results are the same under C and C.UTF-8.
    /// </summary>
    private Command SevenZip(params string[] arguments) => Cli
        .Wrap(_exePath)
        .WithArguments(arguments)
        .WithEnvironmentVariables(environment => environment.Set("LC_ALL", "C.UTF-8"));

    /// <summary>
    /// Extracts with 7z, then makes the result match <see cref="ArchiveEntryPath.Clean"/> and
    /// <see cref="EntryNames"/>. Only entries whose names 7z would place differently are handled
    /// separately, so an archive with well-formed names is a single 7z run.
    /// </summary>
    private async ValueTask ExtractArchive(AbsolutePath source, string archiveName, AbsolutePath destination, CancellationToken cancellationToken)
    {
        var plan = new ArchiveEntryPlan(await ListEntries(source, cancellationToken));
        if (plan.UnaddressableCount > 0)
        {
            _logger.LogWarning("Archive `{Archive}` stores names that are not valid UTF-8 and end in whitespace, which 7z cannot select by name; they are extracted with the rest and renamed afterwards", archiveName);
        }

        var root = destination.ToNativeSeparators(_osInformation);

        // Links the folder already holds are not the archive's: the sweep below leaves them alone.
        var existingLinks = ExtractedLinks.FindLinks(root).ToHashSet(StringComparer.Ordinal);
        var belowLinks = EntriesBelowLinks(root, plan, archiveName);
        if (plan.ExcludedIndices.Count == 0 && belowLinks.Count == 0)
        {
            await RunExtract(source, archiveName, destination, extraArguments: [], cancellationToken);
        }
        else
        {
            // The listing shows a newline in a name as an underscore, which a literal exclusion would not
            // match, so such an entry would be extracted in place as well as moved. The names then go as
            // patterns where an underscore matches any character.
            var excludedNames = plan.ExcludedIndices.Select(plan.RawName).Concat(belowLinks).ToList();
            var wildcard = excludedNames.Any(MayHideNewline);
            if (wildcard && ClashingEntry(plan, excludedNames) is { } clash)
            {
                _logger.LogWarning("Archive `{Archive}` stores `{Entry}`, which an excluded name with an underscore would also match; the excluded names are matched literally, so a newline in one of them is not recognised", archiveName, clash);
                wildcard = false;
            }

            await using var exclude = await CreateNameArguments(excludedNames, 'x', wildcard, cancellationToken);
            await RunExtract(source, archiveName, destination, [.. wildcard ? Array.Empty<string>() : ["-spd"], .. exclude.Arguments], cancellationToken);
        }

        // Before anything lists the folder with .NET, which cannot see a name that is not valid UTF-8.
        ExtractedNames.Repair(root, plan.Kept, _logger, archiveName);

        // Every link 7z left is gone before the first entry is written by hand: one of those writes could
        // otherwise go through a folder link. The archive may store a file at mode 000, which a copy could not read.
        NormalizeModes(root);
        ExtractedLinks.Sanitize(root, archiveName, _logger, existingLinks);

        if (plan.IsPlain)
        {
            plan.Rewrites.Log(_logger, archiveName);
            return;
        }

        foreach (var folder in plan.Folders)
        {
            if (!SafeExtraction.TryCreateFolder(root, Path.Combine(root, folder))) _logger.LogWarning("Skipping folder `{Entry}` of archive `{Archive}`: a folder on its way is a symbolic link", folder, archiveName);
        }

        foreach (var name in plan.MixedLinkNames) _logger.LogWarning("Dropping `{Entry}` from archive `{Archive}`: it is stored as more than one kind of entry (symbolic link, hard link or something else), and which one wins depends on the format", name, archiveName);

        foreach (var (name, reason) in plan.DroppedHardLinks) _logger.LogWarning("Dropping hard link `{Entry}` from archive `{Archive}`: {Reason}", name, archiveName, reason);

        for (var m = 0; m < plan.Moved.Count; m++)
        {
            var (name, cleaned) = plan.Moved[m];
            var index = plan.MovedIndices[m];
            var target = Path.GetFullPath(Path.Combine(root, cleaned));
            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new FileExtractionException($"Cleaned path `{cleaned}` of entry `{name}` in `{archiveName}` is outside the extraction folder");
            }

            if (!SafeExtraction.TryCreateFolder(root, Path.GetDirectoryName(target) ?? throw new InvalidOperationException($"`{target}` has no parent folder")))
            {
                _logger.LogWarning("Skipping `{Entry}` from archive `{Archive}`: a folder on the way to `{Path}` is a symbolic link", name, archiveName, cleaned);
                continue;
            }

            // The stream or the scratch copy is not given the stored mode, so the execute bit is set from the listing.
            var outcome = await ExtractEntryToFile(source, plan, index, target, plan.IsExecutable(index) ? Executable : RegularFile, cancellationToken);
            if (outcome.Error is not null) throw new FileExtractionException($"7z failed extracting `{name}` from `{archiveName}`: {outcome.Error}");
        }

        var placeable = new List<(string Name, string Path, string Target)>();
        for (var l = 0; l < plan.Links.Count; l++)
        {
            var (name, cleaned, listedTarget) = plan.Links[l];
            var target = listedTarget ?? await ReadLinkTarget(source, plan, plan.LinkIndices[l], cancellationToken);
            if (target is null) _logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: its target is unreadable or too long", name, archiveName);
            else placeable.Add((name, cleaned, target));
        }

        ExtractedLinks.PlaceAll(root, archiveName, placeable, _logger);

        plan.Rewrites.Log(_logger, archiveName);
    }

    /// <summary>
    /// The target a link entry stores as its data (.7z and .zip list only the mode). Null when the entry
    /// cannot be read or holds more than <see cref="MaxLinkTargetBytes"/> bytes. Of a name stored more than
    /// once, the last entry counts.
    /// </summary>
    private async ValueTask<string?> ReadLinkTarget(AbsolutePath source, ArchiveEntryPlan plan, int index, CancellationToken cancellationToken)
    {
        if (plan.IsRepeated(index))
        {
            await using var scratch = _temporaryFileManager.CreateFile(ext: new Extension(".link"));
            var path = scratch.Path.ToNativeSeparators(_osInformation);

            // 7z refuses a second link of one name, so a repeated link name fails here; a link it did write is
            // never read, since reading the path would follow it.
            var outcome = await ExtractRepeatedEntryTo(source, plan, index, path, mode: null, cancellationToken);
            if (outcome.Error is not null) return null;
            var data = new byte[MaxLinkTargetBytes + 1];
            await using var stream = File.OpenRead(path);
            var read = await stream.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, cancellationToken);
            return read > MaxLinkTargetBytes ? null : Encoding.UTF8.GetString(data.AsSpan(0, read)).TrimEnd('\0');
        }

        // One byte more than the longest target tells "exactly that long" from "longer".
        await using var buffer = new CappedStream(MaxLinkTargetBytes + 1);
        var errors = await ExtractEntryChecked(source, plan, index, buffer, cancellationToken);
        if (errors is not null || buffer.Overflowed || buffer.WrittenSpan.Length > MaxLinkTargetBytes) return null;
        return Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd('\0');
    }

    private const int MaxLinkTargetBytes = 4096;

    /// <summary>A write-only stream that keeps the first bytes written and only notes that there were more.</summary>
    private sealed class CappedStream(int capacity) : Stream
    {
        private readonly byte[] _buffer = new byte[capacity];
        private int _length;

        internal bool Overflowed { get; private set; }

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var kept = Math.Min(capacity - _length, buffer.Length);
            buffer[..kept].CopyTo(_buffer.AsSpan(_length));
            _length += kept;
            if (kept < buffer.Length) Overflowed = true;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value)
        {
            if (value != 0) throw new NotSupportedException();
            _length = 0;
            Overflowed = false;
        }
    }

    /// <summary>
    /// Writes one entry to <paramref name="target"/>. A name stored once is streamed (<c>x -so</c>); a name
    /// stored more than once is extracted into a scratch folder, where 7z overwrites in archive order, so the
    /// last copy is the one kept. Returns null on success and the reason otherwise.
    /// </summary>
    private async ValueTask<EntryOutcome> ExtractEntryToFile(AbsolutePath source, ArchiveEntryPlan plan, int index, string target, UnixFileMode mode, CancellationToken cancellationToken)
    {
        if (plan.IsRepeated(index)) return await ExtractRepeatedEntryTo(source, plan, index, target, mode, cancellationToken);

        // Written beside the target and renamed over it: File.Create(target) would follow a link at the target.
        var temp = SafeExtraction.SiblingTemp(target);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var errors = await ExtractEntryChecked(source, plan, index, output, cancellationToken);
                if (errors is not null) return new EntryOutcome(errors);
            }

            SafeExtraction.Commit(temp, target, mode);
            return new EntryOutcome();
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>How writing one entry went: <see cref="Error"/> when it failed.</summary>
    private readonly record struct EntryOutcome(string? Error = null);

    /// <summary>
    /// Streams one entry and checks it against the listing. The listing prints a newline in a name as an
    /// underscore, so a name that selects nothing is tried again with every underscore as a wildcard. What
    /// 7z writes must be as long as the listed size: a selection that matches nothing still exits 0, and one
    /// that matches several entries writes them one after the other.
    /// </summary>
    private async ValueTask<string?> ExtractEntryChecked(AbsolutePath source, ArchiveEntryPlan plan, int index, Stream output, CancellationToken cancellationToken)
    {
        var name = Encoding.UTF8.GetString(plan.RawName(index));
        for (var wildcard = false; ; wildcard = true)
        {
            var errors = await ExtractEntryTo(source, plan, index, output, wildcard, cancellationToken);
            if (errors is not null) return errors;

            await output.FlushAsync(cancellationToken);
            if (plan.Size(index) is not { } size || output.Length == size || output is CappedStream { Overflowed: true }) return null;
            if (wildcard || !MayHideNewline(plan.RawName(index))) return $"7z wrote {output.Length} bytes for `{name}` but the listing gives {size}";
            output.SetLength(0);
        }
    }

    private async ValueTask<EntryOutcome> ExtractRepeatedEntryTo(AbsolutePath source, ArchiveEntryPlan plan, int index, string target, UnixFileMode? mode, CancellationToken cancellationToken)
    {
        var name = Encoding.UTF8.GetString(plan.RawName(index));
        await using var scratch = _temporaryFileManager.CreateFolder();
        var folder = scratch.Path.ToNativeSeparators(_osInformation);
        for (var wildcard = false; ; wildcard = true)
        {
            foreach (var leftover in Directory.EnumerateFileSystemEntries(folder).ToArray())
            {
                if (!SafeExtraction.IsLink(leftover) && Directory.Exists(leftover)) Directory.Delete(leftover, recursive: true);
                else File.Delete(leftover);
            }

            var errors = await RunEntrySelection(source, plan, index, wildcard, ["-o" + folder], cancellationToken);
            if (errors is not null) return new EntryOutcome(errors);

            // A link is looked at, never opened, moved or chmodded: it can point anywhere. Only a regular file
            // is copied out, and it is copied to a fresh sibling of the target, which the rename replaces.
            var (files, links) = ListExtracted(folder);
            if (files.Length == 1 && links.Length == 0 && (plan.Size(index) is not { } size || new FileInfo(files[0]).Length == size))
            {
                SafeExtraction.CopyReplacing(files[0], target, mode);
                return new EntryOutcome();
            }

            if (wildcard || !MayHideNewline(plan.RawName(index)))
            {
                if (links.Length > 0) return new EntryOutcome($"7z wrote a link for `{name}`, which is never followed");
                return new EntryOutcome(files.Length == 1 ? $"7z wrote a file of the wrong size for `{name}`" : $"7z extracted {files.Length} files for `{name}`, expected one");
            }
        }
    }

    /// <summary>
    /// What 7z left in a scratch folder: its regular files and its links. A link is listed and never entered, so a
    /// link to a folder outside does not bring that folder's files into the count.
    /// </summary>
    internal static (string[] Files, string[] Links) ListExtracted(string folder)
    {
        var files = new List<string>();
        var links = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (SafeExtraction.IsLink(entry)) links.Add(entry);
                else if (Directory.Exists(entry)) pending.Push(entry);
                else files.Add(entry);
            }
        }

        return (files.ToArray(), links.ToArray());
    }

    /// <summary>A stored name that is not excluded but matches the pattern of an excluded one, if there is one.</summary>
    private static string? ClashingEntry(ArchiveEntryPlan plan, List<byte[]> excluded)
    {
        var patterns = excluded.Select(WildcardPattern).Distinct().ToList();
        for (var i = 0; i < plan.Count; i++)
        {
            var raw = plan.RawName(i);
            if (excluded.Any(e => e.AsSpan().SequenceEqual(raw))) continue;
            var name = Encoding.UTF8.GetString(raw);
            if (patterns.Any(p => p.Length == name.Length && p.Zip(name).All(pair => pair.First == '?' || pair.First == pair.Second))) return name;
        }

        return null;
    }

    /// <summary>
    /// The entries of the bulk run that 7z would write through a symbolic link already in the destination: those below
    /// a folder link, and a folder entry that is one. 7z follows such a link, so these are left out of the run and
    /// skipped, as a moved entry is. A file entry at a file link is not here: 7z replaces the link with the file and
    /// never writes into its target. A hard link whose target is left out is left out too: 7z would make it an empty
    /// file. An entry whose name 7z cannot be told is not listed, as it cannot be left out.
    /// </summary>
    private List<byte[]> EntriesBelowLinks(string root, ArchiveEntryPlan plan, string archiveName)
    {
        var blocked = new List<byte[]>();
        var blockedNames = new List<string>();
        foreach (var kept in plan.Kept)
        {
            if (!EntryNames.IsAddressable(kept.Raw) || blocked.Any(b => b.AsSpan().SequenceEqual(kept.Raw))) continue;

            var name = Encoding.UTF8.GetString(kept.Raw);
            if (kept.HardLinkTarget is { } target
                && blockedNames.Any(b => target == b || target.StartsWith(b.TrimEnd('/') + "/", StringComparison.Ordinal)))
            {
                blocked.Add(kept.Raw);
                blockedNames.Add(name);
                _logger.LogWarning("Skipping `{Entry}` from archive `{Archive}`: its target `{Target}` is skipped because 7z would write it through a symbolic link in the destination", name, archiveName, target);
                continue;
            }

            var segments = ArchiveEntryPlan.PlacedBy7z(name).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (FirstLink(root, kept.IsDirectory ? segments : segments[..Math.Max(0, segments.Length - 1)]) is not { } link) continue;

            blocked.Add(kept.Raw);
            blockedNames.Add(name);
            _logger.LogWarning("Skipping `{Entry}` from archive `{Archive}`: `{Link}` in the destination is a symbolic link, and 7z would write through it", name, archiveName, link);
        }

        return blocked;
    }

    /// <summary>The first existing component of <paramref name="segments"/> below <paramref name="root"/> that is a symbolic link, or null.</summary>
    private static string? FirstLink(string root, string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (SafeExtraction.IsLink(current)) return current;
            if (!Directory.Exists(current)) return null;
        }

        return null;
    }

    private static string WildcardPattern(byte[] raw) => Encoding.UTF8.GetString(WildcardBytes(raw));

    private static byte[] WildcardBytes(byte[] name) => name.Select(b => b is (byte)'_' or (byte)'*' or (byte)'?' ? (byte)'?' : b).ToArray();

    private static bool MayHideNewline(byte[] raw) => raw.Contains((byte)'_');

    private ValueTask<string?> ExtractEntryTo(AbsolutePath source, ArchiveEntryPlan plan, int index, Stream output, bool wildcard, CancellationToken cancellationToken) =>
        RunEntrySelection(source, plan, index, wildcard, ["-so"], cancellationToken, output);

    /// <summary>
    /// Runs 7z on the one entry (<c>-i</c>), leaving out what is stored below it. With <paramref name="wildcard"/>
    /// the names are patterns in which every underscore matches any character. Returns null on success and 7z's
    /// error text otherwise.
    /// </summary>
    private async ValueTask<string?> RunEntrySelection(AbsolutePath source, ArchiveEntryPlan plan, int index, bool wildcard, string[] destination, CancellationToken cancellationToken, Stream? output = null)
    {
        await using var include = await CreateNameArguments([plan.RawName(index)], 'i', wildcard, cancellationToken);
        await using var exclude = await CreateNameArguments(plan.IndicesBelow(index).Select(plan.RawName), 'x', wildcard, cancellationToken);

        var errors = new List<string>();
        var command = SevenZip(["x", .. destination, "-bso0", "-bsp0", "-y", .. wildcard ? Array.Empty<string>() : ["-spd"], .. include.Arguments.Concat(exclude.Arguments).Distinct(), source.ToNativeSeparators(_osInformation)])
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) errors.Add(line);
            }))
            .WithValidation(CommandResultValidation.None);
        if (output is not null) command = command.WithStandardOutputPipe(PipeTarget.ToStream(output));

        var result = await _processRunner.RunAsync(command, logOutput: false, cancellationToken: cancellationToken);
        return result.ExitCode == 0 ? null : $"7z exited with code {result.ExitCode}: {string.Join(" | ", errors)}";
    }

    /// <summary>The arguments that select (<c>i</c>) or leave out (<c>x</c>) some entries, and the list file they use.</summary>
    private sealed class NameArguments(string[] arguments, TemporaryPath? listFile) : IAsyncDisposable
    {
        internal string[] Arguments => arguments;

        public async ValueTask DisposeAsync()
        {
            if (listFile is { } file) await file.DisposeAsync();
        }
    }

    /// <summary>
    /// Names go to 7z as the bytes the listing printed, one per line in a list file read as code page
    /// 437 (<c>-scsDOS</c>), which carries every byte through unchanged and so matches raw and UTF-8
    /// names alike. 7z trims whitespace from the end of a list line, so a name that ends in whitespace
    /// goes on the command line instead, where it must be valid UTF-8. <c>-spd</c> (set by the caller)
    /// makes every name literal.
    /// </summary>
    private async ValueTask<NameArguments> CreateNameArguments(IEnumerable<byte[]> names, char kind, CancellationToken cancellationToken) =>
        await CreateNameArguments(names, kind, wildcard: false, cancellationToken);

    /// <param name="wildcard">Turn each name into a pattern where an underscore (what the listing shows for a newline) and any wildcard character match one character.</param>
    private async ValueTask<NameArguments> CreateNameArguments(IEnumerable<byte[]> names, char kind, bool wildcard, CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        var listed = new List<byte>();
        foreach (var stored in names)
        {
            var name = wildcard ? WildcardBytes(stored) : stored;
            if (EntryNames.EndsInWhitespace(name))
            {
                arguments.Add($"-{kind}!{Encoding.UTF8.GetString(name)}");
                continue;
            }

            listed.AddRange(name);
            listed.Add((byte)'\n');
        }

        if (listed.Count == 0) return new NameArguments([.. arguments], listFile: null);

        var file = _temporaryFileManager.CreateFile(ext: new Extension(".txt"));
        var path = file.Path.ToNativeSeparators(_osInformation);
        await File.WriteAllBytesAsync(path, listed.ToArray(), cancellationToken);
        return new NameArguments(["-scsDOS", $"-{kind}@{path}", .. arguments], file);
    }

    /// <summary>
    /// Lists the entries with their names as stored. <c>-slt</c> prints one "Path = " block per entry, and
    /// the plain table would hide a leading "../" behind column alignment. The output is kept as bytes:
    /// a name that is not valid UTF-8 is printed as it is stored and would not survive a string.
    /// </summary>
    private async ValueTask<List<ArchiveEntryPlan.Entry>> ListEntries(AbsolutePath source, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var command = SevenZip("l", "-slt", "-ba", "-sccUTF-8", source.ToNativeSeparators(_osInformation))
            .WithStandardOutputPipe(PipeTarget.ToStream(output))
            .WithValidation(CommandResultValidation.ZeroExitCode);

        await _processRunner.RunAsync(command, logOutput: false, cancellationToken: cancellationToken);
        return ParseListing(output.GetBuffer().AsSpan(0, (int)output.Length));
    }

    internal static List<ArchiveEntryPlan.Entry> ParseListing(ReadOnlySpan<byte> listing)
    {
        var entries = new List<ArchiveEntryPlan.Entry>();
        byte[]? name = null;
        var isDirectory = false;
        byte[]? symlinkTarget = null;
        byte[]? hardLinkTarget = null;
        var isSymlink = false;
        long? size = null;
        var isExecutable = false;

        void Flush()
        {
            if (name is not null)
            {
                entries.Add(new ArchiveEntryPlan.Entry(
                    EntryNames.Decode(name), isDirectory,
                    symlinkTarget is null ? null : EntryNames.Decode(symlinkTarget),
                    hardLinkTarget is null ? null : EntryNames.Decode(hardLinkTarget),
                    isSymlink, name, size, isExecutable));
            }

            name = null;
            isDirectory = false;
            symlinkTarget = null;
            hardLinkTarget = null;
            isSymlink = false;
            size = null;
            isExecutable = false;
        }

        // tar prints the target and the mode; .7z, .zip (and presumably rar) print only the mode string, whose
        // first character is `l` for a symbolic link.
        static byte[]? NonEmpty(ReadOnlySpan<byte> value) => value.Length == 0 ? null : value.ToArray();
        static string? UnixMode(ReadOnlySpan<byte> value)
        {
            var mode = Encoding.UTF8.GetString(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return mode is { Length: 10 } ? mode : null;
        }

        static bool TryField(ReadOnlySpan<byte> line, ReadOnlySpan<byte> prefix, out ReadOnlySpan<byte> value)
        {
            value = line.StartsWith(prefix) ? line[prefix.Length..] : default;
            return line.StartsWith(prefix);
        }

        while (!listing.IsEmpty)
        {
            var end = listing.IndexOf((byte)'\n');
            var line = end < 0 ? listing : listing[..end];
            listing = end < 0 ? default : listing[(end + 1)..];

            if (TryField(line, "Path = "u8, out var value))
            {
                Flush();
                name = value.ToArray();
            }
            else if (TryField(line, "Folder = "u8, out value))
            {
                isDirectory = value.SequenceEqual("+"u8);
            }
            else if (TryField(line, "Symbolic Link = "u8, out value))
            {
                symlinkTarget = NonEmpty(value);
                isSymlink |= symlinkTarget is not null;
            }
            else if (TryField(line, "Hard Link = "u8, out value))
            {
                hardLinkTarget = NonEmpty(value);
            }
            else if (TryField(line, "Size = "u8, out value))
            {
                size = long.TryParse(Encoding.ASCII.GetString(value), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            }
            else if (TryField(line, "Mode = "u8, out value) || TryField(line, "Attributes = "u8, out value))
            {
                // A .7z entry carries no Folder line, only the letters 7z gives its attributes, D among them for a folder.
                var letters = value.IndexOf((byte)' ') is >= 0 and var space ? value[..space] : value;
                isDirectory |= letters.Contains((byte)'D');
                var mode = UnixMode(value);
                isSymlink |= mode is not null && mode[0] == 'l';
                isExecutable |= mode is not null && mode[3] is 'x' or 's';
            }
        }

        Flush();
        return entries;
    }

    private async ValueTask RunExtract(AbsolutePath source, string archiveName, AbsolutePath destination, string[] extraArguments, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // NOTE(erri120): Using "-bsp1" to redirect the progress line to stdout
        var process = SevenZip(["x", "-bsp1", "-y", .. extraArguments, $"-o{destination.ToNativeSeparators(_osInformation)}", $"{source.ToNativeSeparators(_osInformation)}"])
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
        if (AreAllDangerousLinkErrors(errors))
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
#if SMM_USE_SYSTEM_EXTRACTOR
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
