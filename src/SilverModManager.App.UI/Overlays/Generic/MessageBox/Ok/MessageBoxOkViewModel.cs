using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Resources;
using R3;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;

public partial class MessageBoxOkViewModel : AOverlayViewModel<IMessageBoxOkViewModel, Unit>, IMessageBoxOkViewModel
{
    [Reactive] public partial string Title { get; set; } = Language.CancelDownloadOverlayView_Title;

    [Reactive]
    public partial string Description { get; set; } = "This is some very long design only text that spans multiple lines!! This text is super cool!!";

    [Reactive] public required partial IMarkdownRendererViewModel? MarkdownRenderer { get; set; }
    
    /// <summary>
    /// Shows the 'Game is already Running' error when you try to change a game that the app has launched and is still running.
    /// </summary>
    public static Task ShowGameAlreadyRunningError(IServiceProvider serviceProvider, string gameName)
    {
        return Show(serviceProvider, Language.ErrorGameAlreadyRunning_Title, string.Format(Language.ErrorGameAlreadyRunning_Description, gameName));
    }

    /// <summary>
    /// Runs <paramref name="action"/>, and when a game launched from the app is running (the guard on applying,
    /// deleting and removing refuses with <see cref="ExecutableInUseException"/>) shows the 'Game is already
    /// Running' error instead of letting the exception escape. Returns false when the action was refused.
    /// </summary>
    public static async Task<bool> RunOrExplainRunningGame(IServiceProvider serviceProvider, string gameName, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ExecutableInUseException)
        {
            await ShowGameAlreadyRunningError(serviceProvider, gameName);
            return false;
        }
    }

    public static async Task Show(IServiceProvider serviceProvider, string title, string description, string? markdown = null)
    {
        var overlayController = serviceProvider.GetRequiredService<IOverlayController>();
        
        IMarkdownRendererViewModel? markdownRenderer = null;
        if (markdown != null)
        {
            markdownRenderer = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
            markdownRenderer.Contents = markdown;
        }
        
        var viewModel = new MessageBoxOkViewModel
        {
            Title = title,
            Description = description,
            MarkdownRenderer = markdownRenderer,
        };
        await overlayController.EnqueueAndWait(viewModel);
    }
}
