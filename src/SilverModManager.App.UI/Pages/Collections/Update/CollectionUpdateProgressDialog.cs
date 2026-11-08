using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Collections.Update;

public interface ICollectionUpdateProgressViewModel : IViewModelInterface
{
    string Text { get; }
}

/// <summary>The line the progress dialog shows; the work reports into it through <see cref="IProgress{T}"/>.</summary>
public sealed partial class CollectionUpdateProgressViewModel : AViewModel<ICollectionUpdateProgressViewModel>, ICollectionUpdateProgressViewModel, IProgress<string>
{
    public CollectionUpdateProgressViewModel(string text) => Text = text;

    [Reactive] public partial string Text { get; private set; }

    public void Report(string value) => DispatcherHelper.EnsureOnUIThread(() => Text = value);
}

/// <summary>
/// A dialog with a Cancel button that stays up while a slow step runs. The review fetches the new list and hashes
/// archives, so without it pressing "Update to…" would show nothing until that finished.
/// </summary>
public static class CollectionUpdateProgressDialog
{
    public static (SilverModManager.App.UI.Dialog.Dialog Dialog, CollectionUpdateProgressViewModel Model) Create(string title, string text)
    {
        var model = new CollectionUpdateProgressViewModel(text);
        var dialog = DialogFactory.CreateDialog(title, [DialogStandardButtons.Cancel], model, DialogWindowSize.Small);
        return (dialog, model);
    }

    /// <summary>
    /// Runs <paramref name="work"/> behind the dialog. Returns null when the user pressed Cancel (the token passed to
    /// the work is tripped, and a result that still arrives is dropped); throws if <paramref name="cancellationToken"/>
    /// itself is cancelled.
    /// </summary>
    public static async Task<T?> Run<T>(
        IWindowManager windowManager,
        string title,
        string text,
        Func<IProgress<string>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var (dialog, model) = Create(title, text);
        var shown = windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        _ = shown.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result.ButtonId == ButtonDefinitionId.Cancel) cts.Cancel();
        }, TaskScheduler.Default);

        bool UserCancelled() => cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        try
        {
            var result = await work(model, cts.Token);
            return UserCancelled() ? null : result;
        }
        catch (OperationCanceledException) when (UserCancelled())
        {
            return null;
        }
        finally
        {
            dialog.Close();
        }
    }
}
