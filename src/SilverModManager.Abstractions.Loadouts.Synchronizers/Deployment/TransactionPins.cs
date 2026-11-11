using System.Runtime.CompilerServices;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// File store pins that live as long as a transaction: bytes stored for a row the transaction adds stay out of the
/// collector's reach until the transaction is committed or dropped. The owner of the transaction opens a hold next
/// to it; code that only has the transaction hands its pins over through <see cref="Keep"/>.
/// </summary>
public static class TransactionPins
{
    // Keyed on the transaction like DeploymentPlanStore's pending entries, so an abandoned transaction takes its
    // list with it.
    private static readonly ConditionalWeakTable<ITransaction, List<IDisposable>> Holds = new();

    /// <summary>Opens the hold for <paramref name="tx"/>. Disposing it releases every pin kept for the transaction.</summary>
    public static IDisposable Hold(ITransaction tx)
    {
        var pins = new List<IDisposable>();
        lock (Holds)
        {
            if (Holds.TryGetValue(tx, out _))
                throw new InvalidOperationException("A hold is already open for this transaction");
            Holds.Add(tx, pins);
        }
        return new Scope(tx, pins);
    }

    /// <summary>
    /// Keeps <paramref name="pin"/> until the hold on <paramref name="tx"/> is disposed. With no hold open it
    /// disposes the pin and throws: a pin nobody releases is a leak, not a safety net.
    /// </summary>
    public static void Keep(ITransaction tx, IDisposable pin)
    {
        List<IDisposable>? pins;
        lock (Holds)
        {
            if (!Holds.TryGetValue(tx, out pins))
            {
                pin.Dispose();
                throw new InvalidOperationException("No hold is open for this transaction, so nothing would release the pin");
            }
            pins.Add(pin);
        }
    }

    private sealed class Scope(ITransaction tx, List<IDisposable> pins) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            IDisposable[] toRelease;
            lock (Holds)
            {
                if (_disposed) return;
                _disposed = true;
                Holds.Remove(tx);
                toRelease = pins.ToArray();
                pins.Clear();
            }
            foreach (var pin in toRelease)
                pin.Dispose();
        }
    }
}
