namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Caps how many bytes of work are in flight at once. A count of workers does not bound memory when one worker may hold
/// a few megabytes and the next a few hundred; each piece of work reserves what it expects to hold and gives it back
/// when done. Reservations are admitted in the order they asked, so a large one is not starved by small ones. One larger
/// than the whole budget is admitted when nothing else is reserved, since refusing it would stop the install.
/// </summary>
public sealed class ByteBudget(long capacity)
{
    private const long MaxDefault = 4L * 1024 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private long _reserved;

    public long Capacity => capacity;

    /// <summary>An eighth of <paramref name="physicalMemory"/>, and no more than four gigabytes.</summary>
    public static long CapacityFor(long physicalMemory) => Math.Min(MaxDefault, physicalMemory / 8);

    /// <summary>The budget for this machine: <see cref="CapacityFor"/> of the memory the runtime may use.</summary>
    public static ByteBudget ForThisMachine() => new(CapacityFor(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

    /// <summary>Waits until <paramref name="bytes"/> fit, then holds them until the returned value is disposed.</summary>
    public ValueTask<IDisposable> Reserve(long bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bytes = Math.Max(0, bytes);
        Waiter waiter;
        lock (_lock)
        {
            if (_waiting.Count == 0 && Fits(bytes))
            {
                _reserved += bytes;
                return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
            }

            waiter = new Waiter(bytes);
            waiter.Node = _waiting.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
            waiter.Registration = cancellationToken.Register(static state => ((Waiter)state!).Owner?.Cancel((Waiter)state!), Link(waiter));
        return new ValueTask<IDisposable>(WaitFor(waiter, cancellationToken));
    }

    private Waiter Link(Waiter waiter)
    {
        waiter.Owner = this;
        return waiter;
    }

    private static async Task<IDisposable> WaitFor(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Source.Task;
        }
        finally
        {
            await waiter.Registration.DisposeAsync();
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_lock)
        {
            if (waiter.Node is { List: not null } node)
            {
                _waiting.Remove(node);
                waiter.Source.TrySetCanceled();
                Admit();
            }
        }
    }

    private bool Fits(long bytes) => _reserved + bytes <= capacity || _reserved == 0;

    private void Release(long bytes)
    {
        lock (_lock)
        {
            _reserved -= bytes;
            Admit();
        }
    }

    // Called with the lock held.
    private void Admit()
    {
        while (_waiting.First is { Value: var next } node && Fits(next.Bytes))
        {
            _waiting.Remove(node);
            _reserved += next.Bytes;
            next.Source.TrySetResult(new Reservation(this, next.Bytes));
        }
    }

    private sealed class Waiter(long bytes)
    {
        public long Bytes { get; } = bytes;
        public TaskCompletionSource<IDisposable> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public ByteBudget? Owner { get; set; }
    }

    private sealed class Reservation(ByteBudget budget, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) budget.Release(bytes);
        }
    }
}
