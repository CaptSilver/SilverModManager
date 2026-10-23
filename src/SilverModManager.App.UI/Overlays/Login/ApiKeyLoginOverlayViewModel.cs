using R3;

namespace NexusMods.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public BindableReactiveProperty<string> ErrorMessage { get; } = new(value: "");
    public ReactiveCommand<Unit> CommandSave { get; }
    public ReactiveCommand<Unit> CommandCancel { get; }

    /// <param name="validate">
    /// Checks the typed key and returns the reason it is unusable, or null when it is fine. The
    /// dialog stays open and shows the reason until a key passes or the user cancels.
    /// </param>
    public ApiKeyLoginOverlayViewModel(Func<string, CancellationToken, ValueTask<string?>> validate)
    {
        // Disabled while a check runs, so a second click cannot start a second request.
        var isChecking = new BindableReactiveProperty<bool>(value: false);

        CommandSave = ApiKey
            .CombineLatest(isChecking, static (key, checking) => !checking && !string.IsNullOrWhiteSpace(key))
            .ToReactiveCommand<Unit>(async (_, cancellationToken) =>
            {
                var key = ApiKey.Value.Trim();
                isChecking.Value = true;
                ErrorMessage.Value = "";
                try
                {
                    var error = await validate(key, cancellationToken);
                    if (error is null) Complete(key);
                    else ErrorMessage.Value = error;
                }
                finally
                {
                    isChecking.Value = false;
                }
            });

        // Close rather than Complete: a cancelled dialog has no result, and the caller reads null as "cancelled".
        CommandCancel = new ReactiveCommand<Unit>(_ => Close());
    }
}
