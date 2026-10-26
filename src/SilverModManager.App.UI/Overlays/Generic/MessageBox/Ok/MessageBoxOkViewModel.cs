using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
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
    /// Runs <paramref name="action"/>, and when a game launched from the app is running (the guard on applying,
    /// deleting and removing refuses with <see cref="ExecutableInUseException"/>) explains that in a dialog instead
    /// of letting the exception escape. Returns false when the action was refused. Other refusals are left to the
    /// caller: the remove-game flow, for one, answers a preflight refusal with a choice of its own.
    /// </summary>
    public static async Task<bool> RunOrExplain(IServiceProvider serviceProvider, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ExecutableInUseException e)
        {
            await serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, "changing a game that is running");
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
