using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A process tracker that reports what the test says is running, so no test reads the real process table.
/// </summary>
public class FakeProcessTracker : IProcessTracker
{
    /// <summary>
    /// Every name <see cref="FindByName"/> was asked about, in order.
    /// </summary>
    public List<string> NameQueries { get; } = [];

    /// <summary>
    /// What <see cref="FindByName"/> returns for a name; nothing is running by default.
    /// </summary>
    public Func<string, IReadOnlyList<TrackedProcess>> ByName { get; set; } = _ => [];

    /// <summary>
    /// What <see cref="FindGameProcesses"/> returns.
    /// </summary>
    public Func<GameInstallation, IReadOnlyList<TrackedProcess>> ForInstallation { get; set; } = _ => [];

    /// <summary>
    /// What <see cref="FindByPrefix"/> returns; nothing is bound to a prefix by default.
    /// </summary>
    public Func<AbsolutePath, IReadOnlyList<TrackedProcess>> ByPrefix { get; set; } = _ => [];

    /// <summary>
    /// The value of <see cref="IsSteamRunning"/>.
    /// </summary>
    public bool SteamRunning { get; set; }

    /// <summary>
    /// What <see cref="FindReaperForApp"/> returns.
    /// </summary>
    public Func<uint, TrackedProcess?> ReaperForApp { get; set; } = _ => null;

    /// <inheritdoc />
    public IReadOnlyList<TrackedProcess> FindGameProcesses(GameInstallation installation) => ForInstallation(installation);

    /// <inheritdoc />
    public IReadOnlyList<TrackedProcess> FindByName(string name)
    {
        lock (NameQueries) NameQueries.Add(name);
        return ByName(name);
    }

    /// <inheritdoc />
    public IReadOnlyList<TrackedProcess> FindByPrefix(AbsolutePath prefix) => ByPrefix(prefix);

    /// <inheritdoc />
    public bool IsSteamRunning() => SteamRunning;

    /// <inheritdoc />
    public TrackedProcess? FindReaperForApp(uint appId) => ReaperForApp(appId);
}

public static class FakeProcessTrackerExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeProcessTracker"/> as the process tracker, unless one is already registered.
    /// The fake itself is registered too, so tests can configure what it reports.
    /// </summary>
    public static IServiceCollection AddFakeProcessTracker(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeProcessTracker>();
        services.TryAddSingleton<IProcessTracker>(s => s.GetRequiredService<FakeProcessTracker>());
        return services;
    }
}
