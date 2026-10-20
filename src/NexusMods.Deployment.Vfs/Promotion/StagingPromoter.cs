using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Moves what the game wrote through the mounted folder into the loadout: staged files into the file
/// store and the Overrides group, whiteouts into reified deletes.
/// </summary>
/// <remarks>
/// Every step after the journal is written can be repeated, so a promotion that dies halfway is
/// finished by <see cref="Recover"/>. The order is: hash, journal, verify, store, record, delete
/// staged files, delete journal. Verification comes before the store because the file store keeps
/// whatever bytes it is given under the hash it is given and never looks at them again. It runs
/// after the session has unmounted, so only another process writing into the staging directory
/// can change a file between verification and the store.
/// </remarks>
public sealed class StagingPromoter
{
    private readonly StagingDirectory _staging;
    private readonly IPromotionSink _sink;
    private readonly IFileHasher _hasher;
    private readonly ILogger<StagingPromoter> _logger;

    public StagingPromoter(StagingDirectory staging, IPromotionSink sink, IFileHasher hasher, ILogger<StagingPromoter> logger)
    {
        _staging = staging;
        _sink = sink;
        _hasher = hasher;
        _logger = logger;
    }

    /// <summary>Promotes everything staged into <paramref name="target"/>.</summary>
    /// <exception cref="InvalidOperationException">A journal from an earlier attempt is still there; <see cref="Recover"/> finishes it.</exception>
    public async Task<PromotionResult> Promote(LoadoutId target, CancellationToken token = default)
    {
        if (_staging.JournalFile.FileExists)
            throw new InvalidOperationException($"An earlier promotion was interrupted and left {_staging.JournalFile}; call Recover before promoting again");

        var files = _staging.Scan().Where(e => !e.IsDirectory).ToList();
        var whiteouts = _staging.ReadWhiteouts();
        if (files.Count == 0 && whiteouts.Count == 0) return PromotionResult.NothingToDo;

        var entries = new List<JournalEntry>(files.Count + whiteouts.Count);
        foreach (var file in files)
        {
            var hash = await _hasher.Hash(_staging.Installation, file.RealPath, token);
            var mode = (uint)File.GetUnixFileMode(file.RealPath.ToString());
            entries.Add(new JournalEntry(file.Path, JournalEntryKind.File, file.Size, hash, mode));
        }
        entries.AddRange(whiteouts.Select(w => new JournalEntry(w, JournalEntryKind.Whiteout, Size.Zero, default, 0)));

        var journal = new PromotionJournal(_staging.Installation, target, entries);
        journal.Write(_staging.JournalFile);

        return await Run(journal, PromotionStatus.Promoted, token);
    }

    /// <summary>
    /// Finishes a promotion that was interrupted. Without a journal there is nothing to finish: files
    /// staged by a session that crashed before promoting are picked up by the next <see cref="Promote"/>.
    /// </summary>
    public async Task<PromotionResult> Recover(CancellationToken token = default)
    {
        if (!_staging.JournalFile.FileExists)
        {
            _logger.LogDebug("No interrupted promotion for installation {Installation}", _staging.Installation);
            return PromotionResult.NothingToDo;
        }

        var journal = PromotionJournal.Read(_staging.JournalFile, _staging.Installation);
        _logger.LogInformation("Finishing an interrupted promotion of {Count} entries into loadout {Loadout}", journal.Entries.Count, journal.Target);
        return await Run(journal, PromotionStatus.Recovered, token);
    }

    private async Task<PromotionResult> Run(PromotionJournal journal, PromotionStatus success, CancellationToken token)
    {
        var files = journal.Entries.Where(e => e.Kind == JournalEntryKind.File).ToList();
        var paths = files.Select(f => f.Path).ToArray();

        if (!journal.Recorded)
        {
            var changed = await FindChanged(files, token);
            if (changed.Count > 0) return Block(journal, paths, changed);

            await _sink.Backup(files.Select(f => (f.Hash, f.Size, (IStreamFactory)new NativeFileStreamFactory(_staging.PathFor(f.Path)))).ToList(), token);
            await _sink.Record(
                journal.Target,
                files.Select(f => new PromotedFile(f.Path, f.Hash, f.Size)).ToList(),
                journal.Entries.Where(e => e.Kind == JournalEntryKind.Whiteout).Select(e => e.Path).ToList(),
                token);

            // Past this point the loadout has the changes and the staged files may already be
            // half deleted, so a replay must not look for them again.
            journal.With(recorded: true).Write(_staging.JournalFile);
        }

        _staging.Clear();
        _staging.JournalFile.Delete();
        return new PromotionResult(success, paths);
    }

    private async Task<List<RelativePath>> FindChanged(IReadOnlyList<JournalEntry> files, CancellationToken token)
    {
        var changed = new List<RelativePath>();
        foreach (var file in files)
        {
            var path = _staging.PathFor(file.Path);
            if (!path.FileExists || await _hasher.Hash(_staging.Installation, path, token) != file.Hash)
                changed.Add(file.Path);
        }
        return changed;
    }

    /// <summary>
    /// Sets the staged files, the whiteout list and the journal aside, so a later promotion starts
    /// from nothing and a person can decide what to do with what the game wrote.
    /// </summary>
    private PromotionResult Block(PromotionJournal journal, IReadOnlyList<RelativePath> paths, IReadOnlyList<RelativePath> changed)
    {
        _logger.LogError("Staged files changed after they were hashed, so nothing was stored: {Files}", string.Join(", ", changed));

        var recovery = NewRecoveryDirectory();
        recovery.CreateDirectory();

        var readme = new StringBuilder()
            .AppendLine("These files were staged by the game, but did not match the hashes recorded when promotion began,")
            .AppendLine("so none of them were stored in the loadout. Something wrote to the staging directory while it was")
            .AppendLine("being promoted, or while the app was closed.")
            .AppendLine()
            .AppendLine("Changed or missing since they were hashed:");
        foreach (var path in changed) readme.AppendLine($"  {path}");
        readme.AppendLine().AppendLine("Everything that was staged:");
        foreach (var path in paths) readme.AppendLine($"  {path}");
        readme.AppendLine().AppendLine("staged/ holds the files at their game-relative paths, whiteouts is the list of deleted paths and")
            .AppendLine("promotion.json is the journal.");
        File.WriteAllText(recovery.Combine("README.txt").ToString(), readme.ToString());

        if (_staging.Root.DirectoryExists()) Directory.Move(_staging.Root.ToString(), recovery.Combine("staged").ToString());
        if (_staging.WhiteoutFile.FileExists) File.Move(_staging.WhiteoutFile.ToString(), recovery.Combine("whiteouts").ToString());
        _staging.Clear();
        // The journal goes last: while it is still in place, a retry sees the same problem and sets the rest aside again.
        File.Move(_staging.JournalFile.ToString(), recovery.Combine("promotion.json").ToString());

        return new PromotionResult(PromotionStatus.Blocked, paths, recovery);
    }

    private AbsolutePath NewRecoveryDirectory()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var directory = _staging.RecoveryRoot.Combine(stamp);
        for (var i = 1; directory.DirectoryExists(); i++) directory = _staging.RecoveryRoot.Combine($"{stamp}-{i}");
        return directory;
    }
}
