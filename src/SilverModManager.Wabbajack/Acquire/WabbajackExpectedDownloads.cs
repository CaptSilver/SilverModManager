using SilverModManager.Library.Pickup;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Keeps <see cref="IExpectedDownloads"/> in step with the plan's rows that wait for the user: a row appears there when
/// it starts waiting and goes when it is held, fails for good or the plan is forgotten. A file that turns up goes to
/// the inbox, which credits whichever row it matches.
/// </summary>
public sealed class WabbajackExpectedDownloads(IArchiveAcquirer acquirer, IExpectedDownloads expected, ArchiveInbox inbox) : IPickupSink, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Name, long Size, FileIdentity Hash), IDisposable> _registered = new();
    private bool _following;

    /// <summary>Starts mirroring the plan. Idempotent.</summary>
    public void Follow()
    {
        lock (_lock)
        {
            if (_following) return;
            _following = true;
            acquirer.PlanChanged += OnPlanChanged;
            acquirer.PlanForgotten += Mirror;
        }
        Mirror();
    }

    /// <inheritdoc />
    public async Task Accept(NexusMods.Paths.AbsolutePath file, Func<string, string, int> link, CancellationToken cancellationToken)
    {
        await inbox.StageCopy(file, link, cancellationToken);
        await inbox.Scan(cancellationToken);
    }

    private void OnPlanChanged(ArchivePlan _) => Mirror();

    private void Mirror()
    {
        lock (_lock)
        {
            if (!_following) return;

            var waiting = acquirer.Plan.Rows
                .Where(row => row.State.WaitsForUser())
                .Select(row => (row.Archive.Name, row.Archive.Size, Hash: row.Archive.XxHash64))
                .ToHashSet();

            // Only the difference moves, so a plan that changes after every finished archive does not end the watch for a moment.
            foreach (var gone in _registered.Keys.Where(key => !waiting.Contains(key)).ToArray())
            {
                _registered[gone].Dispose();
                _registered.Remove(gone);
            }

            foreach (var row in waiting.Where(row => !_registered.ContainsKey(row)))
                _registered[row] = expected.Expect(new ExpectedDownload(row.Name, row.Size, row.Hash, this));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_following) return;
            _following = false;
            acquirer.PlanChanged -= OnPlanChanged;
            acquirer.PlanForgotten -= Mirror;
            foreach (var registration in _registered.Values) registration.Dispose();
            _registered.Clear();
        }
    }
}
