using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Lock;

/// <summary>
/// Checks that the library holds every file a lock names, byte for byte.
/// </summary>
[PublicAPI]
public sealed class LockVerify(IConnection connection, LibraryFileDigests digests)
{
    /// <summary>
    /// A file that is not in the library is a warning, since the lock may simply not have been imported yet. A digest
    /// that does not match, one the lock does not have, or a file whose bytes can no longer be read is a failure: none
    /// of those can be told apart from a swapped file.
    /// </summary>
    /// <remarks>
    /// A file is found by the locked digest first and by its name only after that: a Nexus Mods download keeps the name
    /// of the temporary file it was written to, so the archive name in the lock never matches it.
    /// </remarks>
    public async ValueTask<LockReport> Verify(LockFile lockFile, CancellationToken cancellationToken = default)
    {
        var items = new List<LockReportItem>();
        var files = LibraryFile.All(connection.Db).ToLookup(file => file.FileName.ToString(), StringComparer.Ordinal);

        foreach (var entry in lockFile.Entries)
        {
            if (!Sha256Value.TryParse(entry.Sha256, out var expected))
            {
                items.Add(new LockReportItem(LockLevel.Fail, $"{entry.Id}: the lock has no valid sha256 for {entry.Asset}"));
                continue;
            }

            if (LibraryFile.FindBySha256(connection.Db, expected).Any(file => file.IsValid()))
            {
                items.Add(new LockReportItem(LockLevel.Ok, $"{entry.Id}: sha256 ok"));
                continue;
            }

            var candidates = files[entry.Asset].ToArray();
            if (candidates.Length == 0)
            {
                items.Add(new LockReportItem(LockLevel.Warn, $"{entry.Id}: {entry.Asset} is not in the library"));
                continue;
            }

            items.Add(await Check(entry, candidates, cancellationToken));
        }

        return new LockReport(items);
    }

    private async ValueTask<LockReportItem> Check(LockEntry entry, LibraryFile.ReadOnly[] candidates, CancellationToken cancellationToken)
    {
        string? firstProblem = null;
        foreach (var file in candidates)
        {
            var actual = await digests.EnsureSha256(file, cancellationToken);
            if (Sha256Match.Matches(actual, entry.Sha256))
                return new LockReportItem(LockLevel.Ok, $"{entry.Id}: sha256 ok");

            firstProblem ??= actual is { } digest
                ? $"{entry.Id}: HASH MISMATCH, {entry.Asset} has sha256 {digest} but the lock pins {entry.Sha256}"
                : $"{entry.Id}: {entry.Asset} has no sha256 and its bytes are no longer in the file store";
        }

        return new LockReportItem(LockLevel.Fail, firstProblem ?? throw new InvalidOperationException($"{entry.Id}: there was no library file named {entry.Asset} to check."));
    }
}
