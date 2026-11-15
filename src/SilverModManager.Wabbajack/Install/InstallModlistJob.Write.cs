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
    /// <summary>
    /// Leaves out the directives an earlier run already wrote the file of, so a resume locates and writes only the rest.
    /// <see cref="InstallReconciler.FinishedDirectives"/> decides which are finished; the destination of each is recorded
    /// for merges that read it.
    /// </summary>
    private Placement[] LeaveOutFinishedDirectives(Checked check, WriteState state, Placement[] executable)
    {
        var finished = InstallReconciler.FinishedDirectives(
            state, executable, _keptOriginals, updateMode: _updateMode,
            path => OwnedIniPath(check, path) is not null,
            (directive, stored) => IsLibraryCopyOf(Connection.Db, directive, stored),
            (placement, identity) => state.Written[Fold(placement.To)] = (identity, placement.Directive.XxHash64));

        var done = finished.Select(p => p.Index).ToHashSet();
        if (finished.Length > 0)
            Logger.LogInformation("{Count} of {Total} files of '{List}' are already in the loadout and are kept", finished.Length, executable.Length, check.List.Name);
        return executable.Where(p => !done.Contains(p.Index)).ToArray();
    }

    /// <summary>
    /// A file an earlier version wrote has no list hash to compare. A plain copy of an archive member is the member's
    /// bytes, so it is the list's file when the library file holding those bytes has the list's hash.
    /// </summary>
    internal static bool IsLibraryCopyOf(IDb db, Directive directive, FileIdentity stored)
    {
        if (directive.GetType() != typeof(FromArchive) || !stored.TryXxHash3(out var storeHash)) return false;
        foreach (var file in LibraryFile.FindByHash(db, storeHash))
        {
            if (file.XxHash64Identity() is { } known && known.Equals(directive.XxHash64)) return true;
        }
        return false;
    }

    /// <summary>What producing the bytes of one directive came to, for the one thread that writes rows to take in list order.</summary>
    private abstract record Outcome;

    /// <summary>Nothing for the loadout: a file kept as the user has it, or a tool file already written to its folder.</summary>
    private sealed record NothingToRecord : Outcome
    {
        public static readonly NothingToRecord Instance = new();
    }

    private sealed record FileProduced(FileIdentity Hash, Size Size, FileIdentity XxHash64) : Outcome;

    /// <summary>A file that was written, with a note for each way it is not what the list asked for; each note becomes a <see cref="SkipKind.KeptOriginal"/> row.</summary>
    private sealed record FileProducedWithNotes(FileProduced File, IReadOnlyList<string> Notes) : Outcome;

    private sealed record FileSkipped(SkipKind Kind, string Reason) : Outcome;

    private async Task Write(Checked check, LoadoutItemGroupId groupId, AbsolutePath extrasFolder, WriteState state, IReadOnlyList<Prepared> prepared, CancellationToken cancellationToken)
    {
        var batch = new WriteBatch(Connection, Commits, Writer);
        Progress.Enter(InstallPhase.Write, prepared.Count, prepared.Sum(p => p.Placement.Directive.Size));
        state.ArchiveCount = prepared.Count(p => p.Placement.Directive is CreateBsa);
        var extras = new ExtrasFolder(extrasFolder);
        var budget = MemoryBudget ?? ByteBudget.ForThisMachine();

        try
        {
            // Kinds run one after the other: a later kind reads what an earlier one wrote.
            for (var start = 0; start < prepared.Count;)
            {
                var kind = WriteOrder.Of(prepared[start].Placement.Directive);
                var end = start + 1;
                while (end < prepared.Count && WriteOrder.Of(prepared[end].Placement.Directive) == kind) end++;

                // Merges read each other's output, so they stay in the list's order.
                var workers = kind == WriteOrder.Merge ? 1 : Workers;
                await WriteRun(check, groupId, state, batch, extras, budget, prepared, start, end, workers, cancellationToken);
                start = end;
            }

            await batch.Commit(state, cancellationToken);
        }
        finally
        {
            await batch.Discard();
        }

        var isListOutput = await InstallReconciler.ListOutputRows(FileStore, state, check.List.Name);
        await new LegacyIniTweaks(Connection, FileStore, Logger).KeepUserTweaks(state, TargetLoadout, check.List.Name, isListOutput);
        await RemoveStaleFiles(state, isListOutput);
        if (_updateMode) await RemoveGoneGroups(check, state);
        LogSkipSummary(check, state);
    }

    private const int SkipExamplesLogged = 5;
    // Slower than this, one directive's lookup is worth a line: a long tail with no log shows nothing otherwise.
    private static readonly TimeSpan SlowDirective = TimeSpan.FromSeconds(5);

    private static LogLevel LevelOf(SkipKind kind) => kind.IsByDesign() ? LogLevel.Information : LogLevel.Warning;

    /// <summary>
    /// One line per kind of skip, so a list that skips thousands of files stays readable in the log; each skip also has
    /// its own line. A kind the list leaves out on purpose is Information, any other is a Warning.
    /// </summary>
    private void LogSkipSummary(Checked check, WriteState state)
    {
        foreach (var (kind, paths) in state.Skipped.OrderBy(pair => pair.Key))
        {
            Logger.Log(LevelOf(kind), "{Count} file(s) of '{List}' were skipped as {Kind}, for example: {Examples}",
                paths.Count, check.List.Name, kind, string.Join(", ", paths.Take(SkipExamplesLogged)));
        }
    }

    /// <summary>
    /// Produces the bytes of <paramref name="prepared"/>[<paramref name="start"/>..<paramref name="end"/>] on several
    /// workers, as many as the byte budget lets in, while this thread takes the results in list order and writes the
    /// rows. Producing carries on during a commit, so a slow commit does not hold up the next files.
    /// </summary>
    private async Task WriteRun(Checked check, LoadoutItemGroupId groupId, WriteState state, WriteBatch batch, ExtrasFolder extras, ByteBudget budget,
        IReadOnlyList<Prepared> prepared, int start, int end, int workers, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(workers);
        // Octodiff allocates megabytes of buffers for every patch whatever its size, and patching is mostly copying, so
        // it gets few workers; the garbage it leaves is what a patch-heavy list would otherwise pile up.
        using var patchSlots = new SemaphoreSlim(Math.Max(2, workers / 8));
        var results = Channel.CreateBounded<Task<Outcome>>(new BoundedChannelOptions(Math.Max(256, workers * 4)) { SingleReader = true, SingleWriter = true });
        var admitting = Task.Run(() => Admit(check, state, batch, extras, budget, slots, patchSlots, results.Writer, prepared, start, end, stop.Token), CancellationToken.None);

        try
        {
            for (var i = start; i < end; i++)
            {
                var item = prepared[i];
                if (!await results.Reader.WaitToReadAsync(stop.Token)) break;
                if (!results.Reader.TryRead(out var produced))
                    throw new InvalidOperationException("A result was announced but is not in the queue");
                var outcome = await produced;

                Record(check, groupId, state, batch, item, outcome);
                Progress.Advance(item.Placement.Group, 1, item.Placement.Directive.Size);
                if (batch.IsDue) await batch.Commit(state, cancellationToken);
            }
            await admitting;
        }
        finally
        {
            // Whatever stopped this run, nothing it started may still be writing when the caller moves on.
            await stop.CancelAsync();
            try { await admitting; } catch { /* the failure being reported is the one that stopped the run */ }
            while (results.Reader.TryRead(out var abandoned))
            {
                try { await abandoned; } catch { /* as above */ }
            }
        }
    }

    private async Task Admit(Checked check, WriteState state, WriteBatch batch, ExtrasFolder extras, ByteBudget budget, SemaphoreSlim slots, SemaphoreSlim patchSlots,
        ChannelWriter<Task<Outcome>> results, IReadOnlyList<Prepared> prepared, int start, int end, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = start; i < end; i++)
            {
                var item = prepared[i];
                Task<Outcome> produced;
                if (NeedsNoWork(item))
                {
                    produced = Execute(check, state, batch, extras, item, cancellationToken);
                }
                else
                {
                    var inputs = InputsOf(check, item.Placement.Directive);
                    var patching = item.Placement.Directive is PatchedFromArchive or MergedPatch || inputs.Any(i => i is PatchedFromArchive or MergedPatch);
                    await slots.WaitAsync(cancellationToken);
                    IDisposable reservation;
                    try
                    {
                        if (patching) await patchSlots.WaitAsync(cancellationToken);
                        try
                        {
                            reservation = await budget.Reserve(
                                item.Placement.Directive is CreateBsa
                                    ? item.ArchiveEstimate ?? throw new InvalidOperationException($"The archive build for {item.Placement.Directive.To} was prepared without a work estimate")
                                    : WorkEstimate.Of(item.Placement.Directive, item.Member),
                                cancellationToken);
                        }
                        catch
                        {
                            if (patching) patchSlots.Release();
                            throw;
                        }
                    }
                    catch
                    {
                        slots.Release();
                        throw;
                    }
                    produced = Task.Run(async () =>
                    {
                        try
                        {
                            return await Execute(check, state, batch, extras, item, cancellationToken);
                        }
                        finally
                        {
                            reservation.Dispose();
                            if (patching) patchSlots.Release();
                            slots.Release();
                        }
                    }, CancellationToken.None);
                }

                try
                {
                    await results.WriteAsync(produced, cancellationToken);
                }
                catch
                {
                    try { await produced; } catch { /* stopping anyway */ }
                    throw;
                }
            }
            results.Complete();
        }
        catch (Exception e)
        {
            results.TryComplete(e);
        }
    }

    /// <summary>
    /// The directives an archive is built from, each with the member it reads, so the build can be sized before it starts.
    /// Done in the resolve phase, off the admit loop; the resolver caches, so the build itself finds these already done. An input whose member cannot be
    /// resolved has none here; the build reports why when it reaches that input.
    /// </summary>
    private async Task<IReadOnlyList<(Directive Directive, ResolvedMember? Member)>> ResolveInputMembers(Checked check, Dictionary<FileIdentity, ArchivePlanRow> rows, Directive directive, CancellationToken cancellationToken)
    {
        var inputs = InputsOf(check, directive).ToArray();
        var resolved = new (Directive Directive, ResolvedMember? Member)[inputs.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), options, async (i, token) =>
        {
            ResolvedMember? member = null;
            if (inputs[i] is FromArchive from)
            {
                await InputResolves.WaitAsync(token);
                try
                {
                    member = await ResolveMember(rows, from, token);
                }
                catch (DirectiveSkipped)
                {
                    // Left without a member; BuildArchive reaches this input and reports the skip with its reason.
                }
                finally
                {
                    InputResolves.Release();
                }
            }
            resolved[i] = (inputs[i], member);
        });
        return resolved;
    }

    /// <summary>The directives whose files an archive is built from; none for any other directive.</summary>
    private static IReadOnlyCollection<Directive> InputsOf(Checked check, Directive directive)
        => directive is CreateBsa bsa && check.BsaInputs.TryGetValue(Fold(bsa.TempId), out var inputs)
            ? inputs.Values.Select(p => p.Directive).ToArray()
            : [];

    /// <summary>A directive whose bytes are already in the file store, or that has none to write.</summary>
    private static bool NeedsNoWork(Prepared item)
        => item.Silent || item.Skip is not null || item.Placement.Directive.GetType() == typeof(FromArchive) && item.Placement.Target is not Mo2Target.Extra;

    /// <summary>
    /// Deletes the rows an earlier run made that this run did not ask for again: a file whose content changed has a row
    /// for the old hash next to the new one, and a file the list dropped would otherwise stay installed.
    /// <see cref="InstallReconciler.StaleRows"/> decides which. Runs only after every batch committed, so an install that
    /// failed part way keeps what it had.
    /// </summary>
    private async Task RemoveStaleFiles(WriteState state, Func<GamePath, FileIdentity, bool> isListOutput)
    {
        var (stale, _) = InstallReconciler.StaleRows(state, isListOutput);
        if (stale.Count == 0) return;

        using var tx = Connection.BeginTransaction();
        foreach (var id in stale) tx.Delete(id, recursive: false);
        await tx.Commit();
    }

    /// <summary>
    /// Makes the bytes of one directive and says what they came to. Safe to run for several directives at once: it
    /// writes no rows and touches no group. A directive it cannot execute comes back as <see cref="FileSkipped"/>.
    /// </summary>
    private async Task<Outcome> Execute(Checked check, WriteState state, WriteBatch batch, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var placement = item.Placement;
        if (item.Silent) return NothingToRecord.Instance;

        try
        {
            if (item.Skip is { } skip) throw new DirectiveSkipped(skip.Kind, skip.Reason);
            if (placement.Target is Mo2Target.Extra extra)
            {
                await WriteExtra(check, extras, item, extra, cancellationToken);
                return NothingToRecord.Instance;
            }
            if (placement.Group is null || placement.Path is null)
                throw new InvalidOperationException($"'{placement.To}' reached the write phase without a place in the loadout");

            var (hash, size, xxHash64, notes) = await Produce(check, state, batch, extras, item, cancellationToken);
            state.Written[Fold(placement.To)] = (hash, xxHash64);
            var produced = new FileProduced(hash, size, xxHash64);
            return notes.Count > 0 ? new FileProducedWithNotes(produced, notes) : produced;
        }
        catch (DirectiveSkipped skipped)
        {
            return new FileSkipped(skipped.Kind, skipped.Message);
        }
        catch (PatchException patchFailed)
        {
            return new FileSkipped(SkipKind.Transform, $"the patch for '{placement.To}' could not be applied: {patchFailed.Message}");
        }
        catch (UnsupportedTextureFormatException unsupported)
        {
            return new FileSkipped(SkipKind.Transform, $"the texture '{placement.To}' could not be re-encoded: {unsupported.Message}");
        }
        catch (BethesdaArchiveException archiveFailed)
        {
            return new FileSkipped(SkipKind.Transform, $"the archive '{placement.To}' could not be built: {archiveFailed.Message}");
        }
        catch (ArchiveMismatchException mismatch)
        {
            return new FileSkipped(SkipKind.Transform, $"the archive '{placement.To}' was built wrong: {mismatch.Message}");
        }
        catch (MemberNotFoundException notFound)
        {
            return new FileSkipped(SkipKind.MemberNotFound, notFound.Message);
        }
    }

    /// <summary>Writes the rows for what <paramref name="outcome"/> says about <paramref name="item"/>. Only the thread that takes results in list order calls this.</summary>
    private void Record(Checked check, LoadoutItemGroupId modlistGroup, WriteState state, WriteBatch batch, Prepared item, Outcome outcome)
    {
        var placement = item.Placement;
        batch.Items++;
        batch.Bytes += placement.Directive.Size;
        if (outcome is NothingToRecord) return;

        var tx = batch.Open();
        batch.Dirty = true;
        switch (outcome)
        {
            case FileProduced produced:
                RecordFile(tx, check, modlistGroup, state, placement, produced);
                break;
            case FileProducedWithNotes withNotes:
                // The file row goes in first: AddSkip only marks earlier rows wanted, it retracts nothing.
                RecordFile(tx, check, modlistGroup, state, placement, withNotes.File);
                foreach (var note in withNotes.Notes)
                    AddSkip(tx, modlistGroup, state, placement, SkipKind.KeptOriginal, note, check);
                break;
            case FileSkipped skipped:
                if (placement.Group is { } folder) Progress.MarkFailed(folder, skipped.Kind, skipped.Reason);
                AddSkip(tx, modlistGroup, state, placement, skipped.Kind, skipped.Reason, check);
                break;
            default:
                throw new InvalidOperationException($"No rows are written for a {outcome.GetType().Name}");
        }
    }

    private void RecordFile(ITransaction tx, Checked check, LoadoutItemGroupId modlistGroup, WriteState state, Placement placement, FileProduced produced)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path)
            throw new InvalidOperationException($"'{placement.To}' was produced without a place in the loadout");
        if (placement.Target is Mo2Target.ProfileSetting && OwnedIniPath(check, path) is { } iniPath)
            SetIniBaseline(tx, modlistGroup, state, groupName, check, iniPath, produced.Hash);
        else
            AddFile(tx, modlistGroup, state, groupName, path, produced.Hash, produced.Size, placement.Directive.XxHash64);
    }

    /// <summary>A tool file goes to the list's folder on disk as it is: the loadout never deploys it, so the file store is not involved.</summary>
    private async Task WriteExtra(Checked check, ExtrasFolder extras, Prepared item, Mo2Target.Extra extra, CancellationToken cancellationToken)
    {
        if (item.Placement.Directive is MergedPatch or CreateBsa)
            throw new DirectiveSkipped(SkipKind.ToolFolder, $"'{item.Placement.To}' is built from other files of the list, which a tool folder does not keep");

        var destination = extras.Root.Combine(extra.Path);
        destination.Parent.CreateDirectory();

        // Written beside the destination and renamed over it, so a tool that has the file open keeps its old bytes
        // instead of finding them truncated, and a run that dies halfway leaves the previous file whole.
        var temporary = destination.Parent.Combine($".{Guid.NewGuid():N}.smm-tmp");
        try
        {
            await using (var output = temporary.Create())
            {
                // A tool file has no rows to hold a note, so a kept original there is silent.
                await WriteBytes(check, extras, item.Placement, item.Member, output, parallelism: 1, keepOriginalIfUnsupported: true, notes: [], cancellationToken);
            }

            if (await FileContents.SameBytes(temporary, destination, cancellationToken)) return;

            // A new file starts with the default mode; keep what the user set on the old one (say, an executable script).
            if (destination.FileExists)
                File.SetUnixFileMode(temporary.ToString(), File.GetUnixFileMode(destination.ToString()));
            File.Move(temporary.ToString(), destination.ToString(), overwrite: true);
        }
        finally
        {
            if (temporary.FileExists) temporary.Delete();
        }
    }

    private void AddSkip(ITransaction tx, LoadoutItemGroupId group, WriteState state, Placement placement, SkipKind kind, string reason, Checked check)
    {
        KeepEarlierRows(state, placement);
        _ = new WabbajackSkippedFile.New(tx, out var id)
        {
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = TargetLoadout,
                Name = placement.To.FileName.ToString(),
            },
            GroupId = WabbajackModlistLoadoutGroupId.From(group.Value),
            To = placement.To.ToString(),
            Reason = reason,
            Kind = kind,
        };

        LogSkip(check, placement, kind, reason);
        if (!state.Skipped.TryGetValue(kind, out var skippedOfKind)) state.Skipped[kind] = skippedOfKind = new List<string>();
        skippedOfKind.Add(placement.To.ToString());

        // An archive that could not be built takes its inputs with it: they are not installed anywhere else. A kept
        // original is a note on an archive that was built, so its inputs are all there.
        if (kind != SkipKind.KeptOriginal && placement.Directive is CreateBsa bsa && check.BsaInputs.TryGetValue(Fold(bsa.TempId), out var inputs))
        {
            Progress.AddDropped(inputs.Count);
            Logger.LogWarning("Dropped {Count} input file(s) of '{To}' in '{List}' because the archive was not built ({Reason}): {Inputs}",
                inputs.Count, placement.To, check.List.Name, reason, string.Join(", ", inputs.Values.Take(SkipExamplesLogged).Select(i => i.To.ToString())));
        }
    }

    /// <summary>
    /// Every skip passes through here, so the log names each one with its destination, kind and reason. A file left out
    /// on purpose (a kept original, an archive a tool folder cannot hold) is Information; anything the run failed to
    /// produce is a Warning, since the list stays disabled over it.
    /// </summary>
    private void LogSkip(Checked check, Placement placement, SkipKind kind, string reason)
    {
        Logger.Log(LevelOf(kind), "Skipped '{To}' of '{List}' ({Kind}): {Reason}", placement.To, check.List.Name, kind, reason);
    }

    /// <summary>
    /// A skip means this run could not produce the file, not that the list stopped asking for it, so whatever an earlier
    /// run installed at that path stays until a run can replace it.
    /// </summary>
    private static void KeepEarlierRows(WriteState state, Placement placement)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path) return;
        if (!state.Files.TryGetValue(groupName, out var files)) return;
        foreach (var (_, known) in files.AtPath(path)) known.Wanted = true;
    }

    /// <summary>
    /// The path the loadout's bake owns that <paramref name="path"/> names, spelled as the game spells it, or null when
    /// the game keeps no copy of that file per loadout.
    /// </summary>
    internal static GamePath? OwnedIniPath(Checked check, GamePath path)
    {
        if (check.Loadout.InstallationInstance.Game is not IGameIniFiles game) return null;
        foreach (var owned in GameInis.Paths(game))
        {
            if (owned == path) return owned;
        }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="identity"/> the loadout's baseline for a game INI. A file at the INI's real path would be a
    /// second writer next to the bake, so the profile's INI is kept as the bake's starting point and the settings group
    /// holds only what is not an INI of the game (the loadout's tweak).
    /// </summary>
    private void SetIniBaseline(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, Checked check, GamePath iniPath, FileIdentity identity)
    {
        EnsureGroup(tx, modlistGroup, state, groupName);

        if (GameInis.StateOf(check.Loadout, iniPath) is { } existing)
        {
            tx.Add(existing.Id, LoadoutIniState.Baseline, identity.XxHash3());
            return;
        }

        _ = new LoadoutIniState.New(tx)
        {
            LoadoutId = TargetLoadout,
            Path = iniPath.ToGamePathParentTuple(TargetLoadout),
            Baseline = identity.XxHash3(),
        };
    }

    private LoadoutItemGroupId EnsureGroup(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName)
    {
        if (state.Groups.TryGetValue(groupName, out var group)) return group;

        var isSettings = groupName.Equals(Mo2Layout.ProfileSettingsGroup, StringComparison.OrdinalIgnoreCase);
        var created = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = TargetLoadout,
                Name = isSettings ? Mo2Layout.GameSettingsGroupName : groupName,
                ParentId = modlistGroup,
            },
        };
        if (isSettings) tx.Add(id, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        else tx.Add(id, WabbajackModGroup.Folder, groupName);
        group = state.Groups[groupName] = created.LoadoutItemGroupId;
        state.NewGroups.Add(groupName);
        state.Files[groupName] = new();
        return group;
    }

    private void AddFile(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, GamePath path, FileIdentity identity, Size size, FileIdentity listHash)
    {
        var expected = listHash;
        if (state.Files.TryGetValue(groupName, out var known) && known.TryGet(path, identity, out var existing))
        {
            existing.Wanted = true;
            // A file an earlier version of the app wrote carries no list hash; recording it lets the next run keep the file without reading anything.
            if (existing.ListHash is null && existing.Ids.Count > 0)
            {
                tx.Add(existing.Ids[0], WabbajackInstalledFile.ListHash, expected);
                existing.ListHash = expected;
            }
            return;
        }

        var group = EnsureGroup(tx, modlistGroup, state, groupName);
        known = state.Files[groupName];

        _ = new WabbajackInstalledFile.New(tx, out var fileId)
        {
            ListHash = expected,
            LoadoutFile = new LoadoutFile.New(tx, fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId)
                    {
                        LoadoutId = TargetLoadout,
                        ParentId = group,
                        Name = path.FileName,
                    },
                    TargetPath = path.ToGamePathParentTuple(TargetLoadout),
                },
                Hash = identity.XxHash3(),
                Size = size,
            },
        };
        known.Add(path, identity, new KnownFile { Wanted = true, ListHash = expected, Size = size });
    }
}
