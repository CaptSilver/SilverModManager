using R3;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Pages.SteamShortcut;

/// <summary>
/// The status line and button of the "Show in Steam" settings row. The state is read from Steam's files and the
/// record each time, never stored, so it cannot go stale.
/// </summary>
/// <param name="service">Where the state comes from.</param>
/// <param name="openDialog">Opens the dialog behind the button.</param>
public sealed class SteamShortcutActionSource(ISteamShortcutService service, Func<CancellationToken, Task> openDialog) : IActionButtonSource
{
    private readonly Subject<Unit> _changed = new();

    /// <inheritdoc />
    public Observable<ActionButtonState> State => Observable.Defer(() => _changed.Select(_ => Current).Prepend(Current));

    /// <inheritdoc />
    public ActionButtonState Current
    {
        get
        {
            SteamShortcutStatus status;
            try { status = service.Status(); }
            catch (SteamShortcutRefusedException e) { return new ActionButtonState(e.Message, null); }

            return status.State switch
            {
                SteamShortcutState.NotInSteam => new ActionButtonState("Not in Steam", "Add"),
                SteamShortcutState.InSteam => new ActionButtonState($"In Steam as SilverModManager (account {status.Account?.PersonaName})", "Remove"),
                SteamShortcutState.AddedUnconfirmed => new ActionButtonState("Added through Steam, not yet confirmed", "Remove"),
                SteamShortcutState.AccountUndecided => new ActionButtonState("More than one Steam account; pick one", "Add"),
                SteamShortcutState.NoSteam => new ActionButtonState("No native Steam install", null),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "Unknown shortcut state"),
            };
        }
    }

    /// <inheritdoc />
    public async Task Invoke(CancellationToken cancellationToken)
    {
        try { await openDialog(cancellationToken); }
        finally { _changed.OnNext(Unit.Default); }
    }
}
