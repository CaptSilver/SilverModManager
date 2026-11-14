using R3;

namespace SilverModManager.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> Title { get; }
    public BindableReactiveProperty<string> Instructions { get; }
    public BindableReactiveProperty<string> Watermark { get; }
    public BindableReactiveProperty<string> PageUrl { get; }
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public BindableReactiveProperty<string> ErrorMessage { get; } = new(value: "");
    public ReactiveCommand<Unit> CommandSave { get; }
    public ReactiveCommand<Unit> CommandCancel { get; }

    /// <param name="validate">
    /// Checks the typed key and returns the reason it is unusable, or null when it is fine. The
    /// dialog stays open and shows the reason until a key passes or the user cancels.
    /// </param>
    /// <param name="title">What the dialog is for.</param>
    /// <param name="instructions">Where to get the secret and what happens to it.</param>
    /// <param name="watermark">The hint inside the empty box.</param>
    /// <param name="pageUrl">A page to read the secret from, shown under the instructions; empty for none.</param>
    public ApiKeyLoginOverlayViewModel(
        Func<string, CancellationToken, ValueTask<string?>> validate,
        string title,
        string instructions,
        string watermark,
        string pageUrl = "")
    {
        Title = new BindableReactiveProperty<string>(title);
        Instructions = new BindableReactiveProperty<string>(instructions);
        Watermark = new BindableReactiveProperty<string>(watermark);
        PageUrl = new BindableReactiveProperty<string>(pageUrl);

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
