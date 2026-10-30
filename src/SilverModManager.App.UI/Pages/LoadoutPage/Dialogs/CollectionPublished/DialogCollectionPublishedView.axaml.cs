using System.Net.Mime;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using System.Reactive.Linq;

namespace SilverModManager.App.UI.Pages.LoadoutPage.Dialogs.CollectionPublished;

public partial class DialogCollectionPublishedView : ReactiveUserControl<IDialogCollectionPublishedViewModel>
{
    public DialogCollectionPublishedView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
            {
                this.WhenViewModelValue(vm => vm
                        .WhenAnyValue(x => x.CollectionStatus, x => x.FirstPublish)
                        .Select(tuple => (tuple.Item1, tuple.Item2, vm.CollectionName)))
                    .Subscribe(tuple =>
                        {
                            var (status, firstPublish, collectionName) = tuple;

                            if (firstPublish)
                            {
                                TextDescription.Text = status == CollectionStatus.Listed
                                
                                    ? string.Format(Language.Loadout_Dialog_CollectionPublished_FirstPublish_Listed, collectionName)
                                    : string.Format(Language.Loadout_Dialog_CollectionPublished_FirstPublish_Unlisted, collectionName);

                                TextHelp.Text = string.Format(Language.Loadout_Dialog_CollectionPublished_FirstPublish_Help);
                            }
                            else
                            {
                                TextDescription.Text = string.Format(Language.Loadout_Dialog_CollectionPublished_Revision, collectionName);
                                TextHelp.Text = string.Format(Language.Loadout_Dialog_CollectionPublished_Revision_Help);
                            }
                        }
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel,
                        vm => vm.CollectionUrl,
                        view => view.TextBoxUrl.Text,
                        uri => uri.AbsoluteUri
                    )
                    .DisposeWith(disposables);

                this.BindCommand(ViewModel,
                        vm => vm.CommandCopyUrl,
                        v => v.ButtonCopyToClipboard
                    )
                    .DisposeWith(disposables);
            }
        );
    }
}
