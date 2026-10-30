using System.Reactive.Linq;
using ReactiveUI;

namespace SilverModManager.UI.Sdk;

public static class ViewModelObservation
{
    /// <summary>
    /// <paramref name="select"/> applied to the view's current view model: quiet while there is none,
    /// re-subscribed when it changes, and the old view model's stream dropped when it goes away.
    /// Replaces <c>WhenAnyValue(v => v.ViewModel!.X)</c>, whose expression tree cannot say <c>?.</c>.
    /// The view is taken as <see cref="IViewFor{TViewModel}"/> so the view model type is inferred from
    /// the view, not spelled out at every call.
    /// </summary>
    public static IObservable<T> WhenViewModel<TViewModel, T>(this IViewFor<TViewModel> view, Func<TViewModel, IObservable<T>> select)
        where TViewModel : class
        => view.WhenAnyValue(v => v.ViewModel)
            .Select(vm => vm is null ? Observable.Empty<T>() : select(vm))
            .Switch();

    /// <summary>
    /// <see cref="WhenViewModel{TViewModel,T}"/> for property values: a swap to a view model whose property holds the same
    /// value does not re-emit it, which is what the single WhenAnyValue chain did.
    /// </summary>
    public static IObservable<T> WhenViewModelValue<TViewModel, T>(this IViewFor<TViewModel> view, Func<TViewModel, IObservable<T>> select)
        where TViewModel : class
        => view.WhenViewModel(select).DistinctUntilChanged();
}
