using System.Reactive.Disposables;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Overlays.Login;

public partial class ForumLoginOverlayView : ReactiveUserControl<IForumLoginOverlayViewModel>
{
    public ForumLoginOverlayView()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            if (ViewModel is { } viewModel)
            {
                HeadingTextBlock.Text = $"Sign in to {viewModel.SiteName}";
                WaitingTextBlock.Text = $"Waiting for you to sign in to {viewModel.SiteName} in your browser";
                OpenAgainButton.Text = "Open the page again";
            }

            this.BindCommand<ForumLoginOverlayView, IForumLoginOverlayViewModel, ICommand, Button>(ViewModel, vm => vm.Cancel, v => v.CancelButton)
                .DisposeWith(d);

            this.BindCommand<ForumLoginOverlayView, IForumLoginOverlayViewModel, ICommand, Button>(ViewModel, vm => vm.OpenAgain, v => v.OpenAgainButton)
                .DisposeWith(d);
        });
    }
}
