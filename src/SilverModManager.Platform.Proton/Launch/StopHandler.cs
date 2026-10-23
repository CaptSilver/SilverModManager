using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Catches SIGTERM, SIGINT and SIGHUP for the lifetime of a launch. The first signal is recorded with the
/// phase the launch was in and cancels <see cref="PreparingToken"/>; any later one is logged and dropped,
/// so a second Ctrl-C can't cut a teardown short. What each phase does about a stop is up to the owner.
/// </summary>
public sealed class StopHandler : IDisposable
{
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _preparing = new();
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly object _gate = new();
    private int _signalCount;
    private volatile int _phase = (int)LaunchPhase.Preparing;
    private PosixSignal? _signal;

    /// <summary>
    /// Registers the three handlers. Cancel is set on each so the runtime doesn't terminate the process.
    /// </summary>
    public StopHandler(ILogger logger)
    {
        _logger = logger;
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
    }

    /// <summary>
    /// Cancelled by the first signal, for the code that applies the loadout before the game starts.
    /// </summary>
    public CancellationToken PreparingToken => _preparing.Token;

    /// <summary>
    /// The phase the launch is in; the owner moves it forward as the launch progresses.
    /// </summary>
    public LaunchPhase Phase
    {
        get => (LaunchPhase)_phase;
        set => _phase = (int)value;
    }

    /// <summary>
    /// The first signal received, or null if none has arrived.
    /// </summary>
    public PosixSignal? Signal
    {
        get { lock (_gate) return _signal; }
    }

    /// <summary>
    /// The phase the launch was in when the first signal arrived.
    /// </summary>
    public LaunchPhase? PhaseAtSignal { get; private set; }

    /// <summary>
    /// How many signals have arrived, including the ignored ones.
    /// </summary>
    public int SignalCount => Volatile.Read(ref _signalCount);

    /// <summary>
    /// Raised once, on the first signal, with the signal and the phase it arrived in. Runs on the
    /// runtime's signal thread, so handlers must not block.
    /// </summary>
    public event Action<PosixSignal, LaunchPhase>? Stopping;

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        var phase = Phase;
        bool first;
        lock (_gate)
        {
            Interlocked.Increment(ref _signalCount);
            first = _signal is null;
            if (first)
            {
                _signal = context.Signal;
                PhaseAtSignal = phase;
            }
        }

        if (!first)
        {
            _logger.LogWarning("Ignoring {Signal} while stopping after {First}", context.Signal, Signal);
            return;
        }

        _logger.LogInformation("Received {Signal} during {Phase}", context.Signal, phase);
        _preparing.Cancel();
        Stopping?.Invoke(context.Signal, phase);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _preparing.Dispose();
    }
}
