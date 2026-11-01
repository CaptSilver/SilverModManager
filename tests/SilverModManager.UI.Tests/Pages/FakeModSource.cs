using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A mod source that owns nothing and reports whatever the test sets: no updates until <see cref="HasAnyUpdates"/>
/// or <see cref="Updates"/> say otherwise.
/// </summary>
internal class FakeModSource(string name = "fake") : IModSource
{
    public BehaviorSubject<bool> HasAnyUpdates { get; } = new(false);
    public List<ModUpdate> Updates { get; } = [];

    public string DisplayName => name;
    public bool Owns(LibraryItem.ReadOnly item) => false;
    public Uri? PageUri(LibraryItem.ReadOnly item) => null;
    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => null;
    public virtual Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken) => Task.CompletedTask;
    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item) => Observable.Return(Optional<ModUpdate>.None);
    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items) => Observable.Return(Optional<ModUpdate>.None);
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game) => Updates;
    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => HasAnyUpdates;
}

/// <summary>
/// An update with the numbers the test chooses, for UI code that must not care which source made it.
/// </summary>
internal sealed record FakeModUpdate(int Items, string NewestVersion = "2.0", DateTimeOffset? PublishedAt = null, string CurrentVersion = "1.0")
    : ModUpdate(Enumerable.Range(0, Items).Select(_ => default(LibraryItemId)).ToArray(), CurrentVersion, NewestVersion, PublishedAt);

/// <summary>
/// The host's services with the mod source resolver swapped for one over the test's sources.
/// </summary>
internal sealed class ServicesWithSources(IServiceProvider inner, params IModSource[] sources) : IServiceProvider, IKeyedServiceProvider
{
    private readonly ModSourceResolver _resolver = new(sources);

    public object? GetService(Type serviceType) => serviceType == typeof(ModSourceResolver) ? _resolver : inner.GetService(serviceType);
    public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
}
