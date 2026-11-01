using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A mod source that owns no library item until a test adds some to <see cref="OwnedItems"/>, and reports whatever the test sets: no updates until
/// <see cref="HasAnyUpdates"/> or <see cref="Updates"/> say otherwise. It owns the <see cref="FakeModUpdate"/>s
/// made for its name, records the requests it is asked to apply, and answers each with <see cref="Outcome"/>.
/// </summary>
internal class FakeModSource(string name = "fake") : IModSource
{
    public BehaviorSubject<bool> HasAnyUpdates { get; } = new(false);
    public List<ModUpdate> Updates { get; } = [];

    public string DisplayName => name;
    public List<ModUpdateRequest> Requests { get; } = [];
    public Func<ModUpdateRequest, ModUpdateOutcome> Outcome { get; set; } = static _ => ModUpdateOutcome.Nothing;

    /// <summary>The library items this source owns.</summary>
    public HashSet<EntityId> OwnedItems { get; } = [];

    public bool Owns(LibraryItem.ReadOnly item) => OwnedItems.Contains(item.Id);
    public bool Owns(ModUpdate update) => update is FakeModUpdate fake && fake.Source == name;
    public Uri? PageUri(LibraryItem.ReadOnly item) => null;
    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => null;
    public virtual Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken) => Task.CompletedTask;
    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item) => Observable.Return(Optional<ModUpdate>.None);
    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items) => Observable.Return(Optional<ModUpdate>.None);
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game) => Updates;
    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => HasAnyUpdates;

    /// <summary>False makes the source behave like a free Nexus account: it applies nothing into loadouts.</summary>
    public bool Replaces { get; set; } = true;

    public bool WillReplace(ModUpdateRequest request) => request.Replace && Replaces;

    /// <summary>What the hide action shows for a row of this source; changes land in <see cref="HideRequests"/>.</summary>
    public BehaviorSubject<HideUpdatesState> HideState { get; } = new(new HideUpdatesState(false, 0, false, false));
    public List<(IReadOnlyList<LibraryItem.ReadOnly> Items, bool Hidden)> HideRequests { get; } = [];
    public HideUpdatesLabelKind HideLabel { get; set; } = HideUpdatesLabelKind.Hide;

    public ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
    {
        HideRequests.Add((items, hidden));
        return ValueTask.CompletedTask;
    }

    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items) => HideState;
    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Show : HideLabel;

    public ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        return ValueTask.FromResult(Outcome(request));
    }
}

/// <summary>
/// An update with the numbers the test chooses, for UI code that must not care which source made it.
/// </summary>
internal sealed record FakeModUpdate(IReadOnlyList<LibraryItemId> Ids, string NewestVersion = "2.0", DateTimeOffset? PublishedAt = null, string CurrentVersion = "1.0", string Source = "fake")
    : ModUpdate(Ids, CurrentVersion, NewestVersion, PublishedAt)
{
    /// <summary>An update over <paramref name="items"/> library items that don't exist.</summary>
    public FakeModUpdate(int Items, string NewestVersion = "2.0", DateTimeOffset? PublishedAt = null, string CurrentVersion = "1.0", string Source = "fake")
        : this(Enumerable.Range(0, Items).Select(_ => default(LibraryItemId)).ToArray(), NewestVersion, PublishedAt, CurrentVersion, Source) { }
}

/// <summary>
/// The host's services with the mod source resolver swapped for one over the test's sources, and any other service
/// the test replaces through <see cref="With{TService}"/>.
/// </summary>
internal sealed class ServicesWithSources(IServiceProvider inner, params IModSource[] sources) : IServiceProvider, IKeyedServiceProvider
{
    private readonly ModSourceResolver _resolver = new(sources);

    private readonly Dictionary<Type, object> _replaced = [];

    public ServicesWithSources With<TService>(TService service) where TService : class
    {
        _replaced[typeof(TService)] = service;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ModSourceResolver)) return _resolver;
        return _replaced.TryGetValue(serviceType, out var replaced) ? replaced : inner.GetService(serviceType);
    }
    public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
}
