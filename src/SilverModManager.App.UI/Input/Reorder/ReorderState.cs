using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>
/// The reorder in progress, shared by the pad bindings that drive it: at most one row is lifted at a time. Draws the
/// row lifted, reports a failed move as a toast with the row's real position, and puts the row back if the window
/// goes to the background mid-lift.
/// </summary>
public sealed class ReorderState(IWindowNotificationService notifications, ILogger<ReorderState> logger)
{
    private ReorderLift? _lift;
    private int _pageSize = 1;
    private IDisposable? _subscriptions;

    public bool IsLive => _lift?.Session.IsLive == true;

    public IReorderSession? Session => _lift?.Session;

    /// <summary>How many rows the table holds, for the hint bar's "position n of m".</summary>
    public int Count => _lift?.Count ?? 0;

    public int PageSize => _pageSize;

    /// <summary>Raised when a lift starts or ends or its target moves, so the hint bar can redraw.</summary>
    public event Action? Changed;

    public void Begin(IReorderSource source, Avalonia.Input.IInputElement focused)
    {
        if (IsLive) return;

        ReorderLift? lift;
        try
        {
            lift = source.Begin(focused);
        }
        catch (InvalidOperationException e)
        {
            // The row left the order between the last redraw and the press.
            logger.LogWarning(e, "Could not pick up the focused row to move it");
            notifications.ShowToast("Could not pick that up; it is no longer in the list", ToastNotificationVariant.Failure);
            return;
        }

        if (lift is null) return;

        _pageSize = source.PageSize(focused);
        _lift = lift;
        lift.Row.Classes.Add("Lifted");
        _subscriptions = new CompositeDisposable(
            lift.Session.TargetChanged.Subscribe(_ => Changed?.Invoke()),
            lift.Session.Failed.Subscribe(failure => Failed(lift, failure)));
        Changed?.Invoke();
    }

    public void Nudge(int delta) => _lift?.Session.Nudge(delta);

    public async Task Drop()
    {
        if (_lift is not { } lift) return;

        await lift.Session.Drop();
        End(lift);
    }

    public async Task Cancel()
    {
        if (_lift is not { } lift) return;

        await lift.Session.Cancel();
        End(lift);
    }

    private void Failed(ReorderLift lift, ReorderFailure failure)
    {
        logger.LogWarning(failure.Error, "Could not move the lifted row; it is at position {Position}", failure.ActualIndex + 1);
        var message = failure.WasCancel
            ? $"Could not put it back; it is now at position {failure.ActualIndex + 1}"
            : $"Could not move it; it is at position {failure.ActualIndex + 1}";
        notifications.ShowToast(message, ToastNotificationVariant.Failure);
        End(lift);
    }

    private void End(ReorderLift lift)
    {
        if (!ReferenceEquals(_lift, lift)) return;

        _subscriptions?.Dispose();
        _subscriptions = null;
        _lift = null;
        lift.Row.Classes.Remove("Lifted");
        (lift.Session as IDisposable)?.Dispose();
        Changed?.Invoke();
    }
}
