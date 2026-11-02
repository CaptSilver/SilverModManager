using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// An install tracker whose answer the test chooses per loadout, so a synchronizer test needs no real install job.
/// </summary>
public class FakeLoadoutInstallActivity : ILoadoutInstallActivity
{
    private readonly Dictionary<LoadoutId, BehaviorSubject<bool>> _subjects = new();

    /// <summary>Plays an install into <paramref name="loadout"/> starting or ending.</summary>
    public void SetInstalling(LoadoutId loadout, bool installing) => SubjectOf(loadout).OnNext(installing);

    /// <inheritdoc />
    public bool IsInstallingInto(LoadoutId loadout) => SubjectOf(loadout).Value;

    /// <inheritdoc />
    public IObservable<bool> ObserveInstallingInto(LoadoutId loadout) => SubjectOf(loadout);

    private BehaviorSubject<bool> SubjectOf(LoadoutId loadout)
    {
        lock (_subjects)
        {
            if (!_subjects.TryGetValue(loadout, out var subject)) _subjects[loadout] = subject = new BehaviorSubject<bool>(false);
            return subject;
        }
    }
}

public static class FakeLoadoutInstallActivityExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeLoadoutInstallActivity"/> as the install tracker, unless one is already registered.
    /// The fake itself is registered too, so tests can switch it on.
    /// </summary>
    public static IServiceCollection AddFakeLoadoutInstallActivity(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeLoadoutInstallActivity>();
        services.TryAddSingleton<ILoadoutInstallActivity>(s => s.GetRequiredService<FakeLoadoutInstallActivity>());
        return services;
    }
}
