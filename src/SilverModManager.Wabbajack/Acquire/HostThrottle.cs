using System.Collections.Concurrent;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Caps how many transfers run against one host at once. File hosts rate-limit and ban clients that open many
/// connections, and a modlist often has dozens of archives on the same host, so the acquirer's own parallelism is not
/// enough: each host-specific job takes a slot here for the length of its transfer.
/// </summary>
public sealed class HostThrottle
{
    /// <summary>Transfers allowed at once per host.</summary>
    public const int PerHost = 2;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> Enter(string host, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(host, static _ => new SemaphoreSlim(PerHost));
        await gate.WaitAsync(cancellationToken);
        return new Slot(gate);
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
