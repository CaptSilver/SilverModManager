using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using SilverModManager.Sdk.Diagnostics;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using BethesdaArchiveException = SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException;
using FileState = SilverModManager.Games.Bethesda.Archives.FileState;
using Ba2Dx10FileState = SilverModManager.Games.Bethesda.Archives.Ba2Dx10FileState;
using PatchException = SilverModManager.Wabbajack.Transforms.PatchException;
using UnreadableTextureException = SilverModManager.Wabbajack.Transforms.UnreadableTextureException;
using UnsupportedTextureFormatException = SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

public sealed partial class InstallModlistJob
{
    /// <summary>The list's extras folder and the downloads folder inside it, created when first needed.</summary>
    private sealed class ExtrasFolder(AbsolutePath root)
    {
        public AbsolutePath Root => root;

        public AbsolutePath Downloads
        {
            get
            {
                var downloads = root.Combine("downloads");
                downloads.CreateDirectory();
                return downloads;
            }
        }
    }

    /// <summary>The file store hash and size of what the directive writes, producing and storing the bytes when they are not a member already in the store.</summary>
    private async Task<(FileIdentity Identity, Size Size, FileIdentity XxHash64, IReadOnlyList<string> Notes)> Produce(Checked check, WriteState state, WriteBatch batch, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var directive = item.Placement.Directive;
        switch (directive)
        {
            case FromArchive when directive.GetType() == typeof(FromArchive):
            {
                var member = RequireMember(item.Placement, item.Member);
                return (member.StoreIdentity, member.Size, member.XxHash64 ?? directive.XxHash64, []);
            }

            case MergedPatch merged:
                return WithNotes(await Generated(batch, directive, await MergedBytes(check, state, batch, merged, cancellationToken), checkHash: true, cancellationToken), []);

            case CreateBsa bsa:
            {
                // Inputs the archive could not re-encode are stored as they are; the notes say which.
                var archiveNotes = new List<string>();
                return WithNotes(await Generated(batch, directive, output => BuildArchive(check, state, extras, bsa, item.Placement.Group, output, archiveNotes, cancellationToken), checkHash: false, cancellationToken), archiveNotes);
            }

            default:
                var exempt = directive is RemappedInlineFile or TransformedTexture || IsHashUnchecked(directive);
                // Loose files keep the original bytes when the texture cannot be re-encoded; the notes say which did.
                var notes = new List<string>();
                var file = await Generated(batch, directive, output => WriteBytes(check, extras, item.Placement, item.Member, output, parallelism: 1, keepOriginalIfUnsupported: true, notes, cancellationToken), checkHash: !exempt, cancellationToken);
                return WithNotes(file, notes);
        }
    }

    private static (FileIdentity, Size, FileIdentity, IReadOnlyList<string>) WithNotes((FileIdentity Identity, Size Size, FileIdentity XxHash64) file, IReadOnlyList<string> notes)
        => (file.Identity, file.Size, file.XxHash64, notes);

    internal static bool IsHashUnchecked(Directive directive)
        => directive is InlineFile or ArchiveMeta && HashUnchecked.Contains(directive.To.FileName.ToString(), StringComparer.OrdinalIgnoreCase);

    private async Task<(FileIdentity, Size, FileIdentity)> Generated(WriteBatch batch, Directive directive, Func<Stream, Task> produce, bool checkHash, CancellationToken cancellationToken)
        => await Register(batch, directive, await Writer.Generate(produce, cancellationToken), checkHash, cancellationToken);

    /// <summary>Queues a generated file for the file store, after checking it against the directive's hash when the list promises one.</summary>
    private async Task<(FileIdentity, Size, FileIdentity)> Register(WriteBatch batch, Directive directive, GeneratedFile file, bool checkHash, CancellationToken cancellationToken)
    {
        if (checkHash && !file.XxHash64.Equals(directive.XxHash64))
        {
            await file.DisposeAsync();
            throw new DirectiveSkipped(SkipKind.HashMismatch, $"the file made for '{directive.To}' does not match the one the list was made with");
        }

        // A texture recompressed here need not match the author's bytes; the list never checks it either.
        if (directive is TransformedTexture && !file.XxHash64.Equals(directive.XxHash64))
            Logger.LogInformation("Texture {Path} was recompressed to {Actual} where the list records {Expected}", directive.To, file.XxHash64, directive.XxHash64);

        var (waiting, waitingBytes) = batch.AddPending(file);
        if (waiting >= GeneratedFileWriter.StoreBatchSize || waitingBytes >= Writer.StoreByteLimit) await batch.Flush(cancellationToken);
        return (file.XxHash3, file.Size, file.XxHash64);
    }

    /// <summary>Recompress writes nothing before it throws, but it has read from the member's stream, so open the member again.</summary>
    private async Task KeepOriginal(Placement placement, ResolvedMember? member, Stream output, CancellationToken cancellationToken)
    {
        await using var original = await RequireMember(placement, member).Open();
        await original.CopyToAsync(output, cancellationToken);
    }

    /// <summary>
    /// The bytes of one directive other than a merge or an archive, written to <paramref name="output"/>. A texture whose
    /// format cannot be re-encoded is copied as it is when <paramref name="keepOriginalIfUnsupported"/> allows it, and
    /// <paramref name="notes"/> gets a line saying so; otherwise the exception goes to the caller. <paramref name="named"/>
    /// is the path that line uses when the directive's own path is a temp folder name, as it is for an archive's input.
    /// </summary>
    private async Task WriteBytes(Checked check, ExtrasFolder extras, Placement placement, ResolvedMember? member, Stream output,
        int parallelism, bool keepOriginalIfUnsupported, List<string> notes, CancellationToken cancellationToken, RelativePath? named = null)
    {
        switch (placement.Directive)
        {
            case PatchedFromArchive patched:
            {
                await using var basis = await RequireMember(placement, member).Open();
                await using var delta = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, patched.PatchId, cancellationToken);
                await Transforms.Patch(basis, delta, output, cancellationToken);
                break;
            }
            case TransformedTexture texture:
            {
                try
                {
                    await using var dds = await RequireMember(placement, member).Open();
                    await Transforms.Recompress(dds, texture.Image, output, parallelism, cancellationToken);
                }
                catch (UnreadableTextureException unreadable) when (keepOriginalIfUnsupported)
                {
                    await KeepOriginal(placement, member, output, cancellationToken);
                    notes.Add($"kept the original '{named ?? placement.To}' because it could not be read as a texture: {unreadable.Message}");
                }
                catch (UnsupportedTextureFormatException unsupported) when (keepOriginalIfUnsupported)
                {
                    await KeepOriginal(placement, member, output, cancellationToken);
                    notes.Add($"kept the original '{named ?? placement.To}' because its format cannot be re-encoded to {texture.Image.Format}: {unsupported.Message}");
                }
                break;
            }
            case FromArchive:
            {
                await using var source = await RequireMember(placement, member).Open();
                await source.CopyToAsync(output, cancellationToken);
                break;
            }
            case RemappedInlineFile remapped:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, remapped.SourceDataId, cancellationToken);
                using var reader = new StreamReader(blob, Encoding.UTF8);
                var text = PathMarkers.Substitute(await reader.ReadToEndAsync(cancellationToken), MarkerValues(check, extras), nativeSpelling: false);
                await output.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken);
                break;
            }
            case InlineFile inline:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, inline.SourceDataId, cancellationToken);
                await blob.CopyToAsync(output, cancellationToken);
                break;
            }
            case ArchiveMeta meta:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, meta.SourceDataId, cancellationToken);
                await blob.CopyToAsync(output, cancellationToken);
                break;
            }
            default:
                throw new DirectiveSkipped(SkipKind.Transform, $"a {placement.Directive.GetType().Name} cannot be used as a file of an archive");
        }
    }

    private static ResolvedMember RequireMember(Placement placement, ResolvedMember? member)
        => member ?? throw new InvalidOperationException($"'{placement.To}' reads an archive member that was never resolved");

    private readonly Lock _markerLock = new();
    private PathMarkerValues? _markerValues;

    /// <summary>The values that replace path markers; the same for every file of an install, so worked out once.</summary>
    private PathMarkerValues MarkerValues(Checked check, ExtrasFolder extras)
    {
        lock (_markerLock) return _markerValues ??= WorkOutMarkerValues(check, extras);
    }

    private PathMarkerValues WorkOutMarkerValues(Checked check, ExtrasFolder extras)
    {
        var installation = check.Loadout.InstallationInstance;
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));

        // Without a prefix the translator leaves a Z: path, which Wine maps to the host root.
        var resolution = PrefixResolver.Resolve(installation);
        var prefixRoot = resolution.Prefix?.Path.ToString() ?? "";
        return new PathMarkerValues(
            GamePath: WindowsPathTranslator.Translate(gameRoot.ToString(), prefixRoot),
            Mo2Path: WindowsPathTranslator.Translate(extras.Root.ToString(), prefixRoot),
            DownloadsPath: WindowsPathTranslator.Translate(extras.Downloads.ToString(), prefixRoot),
            StockGameFolders: StockGameFolders(check),
            ListFolders: ListFolders(check));
    }

    /// <summary>The list's stock-game folders, which Apply deploys into the game folder: the ones <see cref="Mo2Layout.Classify"/> sorted files into.</summary>
    internal static HashSet<string> StockGameFolders(Checked check)
        => check.Placements.Select(p => p.Target).OfType<Mo2Target.GameRootFile>()
            .Select(t => t.GroupName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The top-level folders the list's directives write into.</summary>
    private static HashSet<string> ListFolders(Checked check)
        => check.Placements.Select(p => p.To.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sources a merge names, already written by earlier directives, joined in the order the list gives.</summary>
    private async Task<Func<Stream, Task>> MergedBytes(Checked check, WriteState state, WriteBatch batch, MergedPatch merged, CancellationToken cancellationToken)
    {
        var hashes = new List<FileIdentity>(merged.Sources.Count);
        foreach (var source in merged.Sources)
        {
            if (!state.Written.TryGetValue(Fold(source.Path), out var written))
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, was not installed");
            if (!written.XxHash64.Equals(source.XxHash64))
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, is not the file the list was made with");
            hashes.Add(written.Store);
        }

        return async output =>
        {
            // A source this batch generated (a patched or inline file) may still be waiting for the file store.
            await batch.Flush(cancellationToken);
            var joined = await Writer.WriteTemporary(async whole =>
            {
                foreach (var hash in hashes)
                {
                    await using var part = await FileStore.GetFileStream(hash, cancellationToken);
                    await part.CopyToAsync(whole, cancellationToken);
                }
            });
            await using (joined)
            {
                await using var basis = joined.Path.Read();
                await using var delta = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, merged.PatchId, cancellationToken);
                await Transforms.Patch(basis, delta, output, cancellationToken);
            }
        };
    }

    private async Task BuildArchive(Checked check, WriteState state, ExtrasFolder extras, CreateBsa bsa, string? group, Stream output, List<string> notes, CancellationToken cancellationToken)
    {
        var inputs = check.BsaInputs.GetValueOrDefault(Fold(bsa.TempId)) ?? new Dictionary<string, Placement>();
        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var spilled = new List<TemporaryPath>();
        // Building can run for minutes inside one Write item, so the files going in are counted as they are packed.
        var name = bsa.To.FileName.ToString();
        var number = state.NextArchiveNumber();
        Progress.BeginArchive(name, number, state.ArchiveCount, bsa.FileStates.Length);
        if (group is not null) Progress.MarkArchiveBuild(group, number);
        try
        {
            async Task<Stream> OpenInput(RelativePath path, FileState fileState)
            {
                if (!inputs.TryGetValue(Fold(path), out var input))
                    throw new DirectiveSkipped(SkipKind.MemberNotFound, $"the archive '{bsa.To}' needs '{path}', which no directive writes");

                var member = input.Directive is FromArchive from ? await ResolveMember(rows, from, cancellationToken) : null;
                TemporaryPath temporary;
                // A BA2 texture's state records its size, mips and format, so an original that was not re-encoded would not match it.
                var canKeepOriginal = fileState is not Ba2Dx10FileState;
                try
                {
                    temporary = await Writer.WriteTemporary(file => WriteBytes(check, extras, input, member, file, Workers, canKeepOriginal, notes, cancellationToken, named: path));
                }
                catch (UnsupportedTextureFormatException unsupported)
                {
                    // The member's own path says which texture failed; the input's directive path is a temp folder name.
                    var format = input.Directive is TransformedTexture texture ? texture.Image.Format : "unknown";
                    throw new DirectiveSkipped(SkipKind.Transform, $"the archive '{bsa.To}' could not be built: the texture '{path}' in it could not be re-encoded to {format} and its {nameof(Ba2Dx10FileState)} records the size and format, so the original cannot stand in: {unsupported.Message}");
                }
                spilled.Add(temporary);
                return new NotifyingStream(temporary.Path.Read(), () =>
                {
                    Progress.AdvanceArchive(number);
                    if (group is not null) Progress.MarkArchiveBuild(group, number);
                });
            }

            await Transforms.BuildBsa(bsa.State, bsa.FileStates, OpenInput, output, cancellationToken);
        }
        finally
        {
            Progress.EndArchive(number);
            if (group is not null) Progress.MarkArchiveBuild(group, number);
            foreach (var temporary in spilled) await temporary.DisposeAsync();
        }
    }
}
