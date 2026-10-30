using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Controls;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

public partial class WabbajackModlistCardView : ReactiveUserControl<IWabbajackModlistCardViewModel>
{
    public WabbajackModlistCardView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.Name, view => view.TitleText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Kind, view => view.KindText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Source, view => view.SourceText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Author, view => view.AuthorText.Text, author => string.IsNullOrEmpty(author) ? "" : $"by {author}").DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.GameName, view => view.GameText.Text, game => game).DisposeWith(d);

            // The view model is fixed for a card's life, so its sizes are read once.
            if (ViewModel is { } card)
                SizeText.Text = $"Version {card.Version}, {card.ArchiveSize} to download, {card.InstallSize} installed";

            Models()
                .Select(card => card.WhenAnyValue(vm => vm.ArchivesHad, vm => vm.ArchivesNeeded))
                .Switch()
                .SubscribeWithErrorLogging(t => ArchivesText.Text = $"{t.Item1:N0} of {t.Item2:N0} archives in the library")
                .DisposeWith(d);

            Models()
                .Select(card => card.WhenAnyValue(vm => vm.State))
                .Switch()
                .SubscribeWithErrorLogging(state =>
                {
                    StateText.Text = state switch
                    {
                        WabbajackModlistCardState.Installed => "Installed",
                        WabbajackModlistCardState.Incomplete => "Install incomplete",
                        _ => "",
                    };
                    InstallButton.IsVisible = state == WabbajackModlistCardState.NotInstalled;
                })
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.InstallButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.OpenCommand, view => view.OpenButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RemoveCommand, view => view.RemoveButton).DisposeWith(d);
        });
    }

    private IObservable<IWabbajackModlistCardViewModel> Models() => this.WhenAnyValue(view => view.ViewModel).WhereNotNull();
}
