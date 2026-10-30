using NSubstitute;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Library;
using NexusMods.Hashing.xxHash3;
using Xunit;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>Wabbajack settings that live in a field, so a test can read back what the code under test wrote.</summary>
internal static class MemorySettings
{
    public static ISettingsManager Create()
    {
        var current = new WabbajackSettings();
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<WabbajackSettings>(Arg.Any<string?>()).Returns(_ => current);
        settings.Update(Arg.Any<Func<WabbajackSettings, WabbajackSettings>>(), Arg.Any<string?>())
            .Returns(call => current = call.Arg<Func<WabbajackSettings, WabbajackSettings>>()(current));
        return settings;
    }
}

/// <summary>Remembers every message, so a test can check what a service told the log.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }

    public string[] Messages(LogLevel level)
    {
        lock (Entries) return Entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();
    }
}

internal sealed class FakePlanner : IModlistPlanner
{
    public List<(LibraryFileId File, LoadoutId? Target)> Built { get; } = [];
    public ArchivePlan? Plan { get; set; }
    public Exception? Failure { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ArchivePlan> BuildPlan(LibraryFileId file, LoadoutId? target, IProgress<PlanProgress>? progress, CancellationToken cancellationToken)
    {
        lock (Built) Built.Add((file, target));
        if (Gate is { } gate) await gate.Task;
        if (Failure is { } failure) throw failure;
        return (Plan ?? new ArchivePlan([])) with { Modlist = file };
    }
}

internal sealed class FakeCatalog(params ModlistInfo[] modlists) : IModlistCatalog
{
    public IReadOnlyList<ModlistInfo> List() => modlists;

    public ModlistInfo Find(string query)
        => modlists.FirstOrDefault(m => string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase) || m.HexId == query)
           ?? throw new ModlistNotKnownException($"'{query}' is not a modlist the app knows.");

    public static ModlistInfo Info(ulong id, string name, string version = "1.0")
        => new(LibraryFileId.From(EntityId.From(id)), name, version, "SkyrimSpecialEdition", 10, name.Replace(' ', '-') + ".wabbajack");
}

/// <summary>An acquirer that only records what it is asked and finishes when the test says so.</summary>
internal sealed class RecordingAcquirer : IArchiveAcquirer
{
    public ArchivePlan Plan { get; private set; } = new([]);
    public AcquireStatus? Status { get; set; }
    public event Action<ArchivePlan>? PlanChanged { add { } remove { } }
    public event Action<AcquireStatus>? StatusChanged { add { } remove { } }
    public event Action? PlanForgotten { add { } remove { } }

    public List<ArchivePlan> Runs { get; } = [];
    public List<CancellationToken> RunTokens { get; } = [];
    public int Cancels { get; private set; }
    public TaskCompletionSource? RunGate { get; set; }
    public Exception? RunFailure { get; set; }

    public void Show(ArchivePlan plan) => Plan = plan;

    public async Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
    {
        lock (Runs) { Runs.Add(plan); RunTokens.Add(cancellationToken); }
        Plan = plan;
        if (RunGate is { } gate) await gate.Task;
        if (RunFailure is { } failure) throw failure;
    }

    public void Cancel() => Cancels++;
    public int OpenNext(int count) => 0;
    public IDisposable Attach() => System.Reactive.Disposables.Disposable.Empty;
    public void Recheck() { }
    public void Release() { }
}
