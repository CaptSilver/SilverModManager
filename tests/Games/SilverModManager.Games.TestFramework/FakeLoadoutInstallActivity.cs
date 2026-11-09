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
    private readonly Dictionary<LoadoutId, BehaviorSubject<bool>> _writing = new();

    /// <summary>Plays an install into <paramref name="loadout"/> starting or ending.</summary>
    public void SetInstalling(LoadoutId loadout, bool installing)
    {
        WritingOf(loadout).OnNext(installing);
        SubjectOf(loadout).OnNext(installing);
    }

    /// <summary>Plays an update that stopped part way: the loadout cannot be applied, but nothing is writing into it.</summary>
    public void SetStalled(LoadoutId loadout)
    {
        WritingOf(loadout).OnNext(false);
        SubjectOf(loadout).OnNext(true);
    }

    /// <inheritdoc />
    public bool IsInstallingInto(LoadoutId loadout) => SubjectOf(loadout).Value;

    /// <inheritdoc />
    public IObservable<bool> ObserveInstallingInto(LoadoutId loadout) => SubjectOf(loadout);

    /// <inheritdoc />
    public IObservable<bool> ObserveWritingInto(LoadoutId loadout) => WritingOf(loadout);

    private BehaviorSubject<bool> SubjectOf(LoadoutId loadout) => Of(_subjects, loadout);

    private BehaviorSubject<bool> WritingOf(LoadoutId loadout) => Of(_writing, loadout);

    private static BehaviorSubject<bool> Of(Dictionary<LoadoutId, BehaviorSubject<bool>> subjects, LoadoutId loadout)
    {
        lock (subjects)
        {
            if (!subjects.TryGetValue(loadout, out var subject)) subjects[loadout] = subject = new BehaviorSubject<bool>(false);
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
