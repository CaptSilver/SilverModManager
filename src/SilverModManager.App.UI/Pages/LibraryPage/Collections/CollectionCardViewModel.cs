using Avalonia.Media.Imaging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Resources;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

public class CollectionCardViewModel : AViewModel<ICollectionCardViewModel>, ICollectionCardViewModel
{
    private readonly CollectionRevisionMetadata.ReadOnly _revision;
    private readonly CollectionMetadata.ReadOnly _collection;

    public CollectionCardViewModel(
        CollectionDownloader collectionDownloader,
        IResourceLoader<EntityId, Bitmap> tileImagePipeline,
        IResourceLoader<EntityId, Bitmap> userAvatarPipeline,
        IWindowManager windowManager,
        WorkspaceId workspaceId,
        CollectionRevisionMetadata.ReadOnly revision,
        LoadoutId targetLoadout)
    {
        _revision = revision;
        _collection = _revision.Collection;

        var workspaceController = windowManager.ActiveWorkspaceController;

        OpenCollectionDownloadPageCommand = new ReactiveCommand<NavigationInformation>(execute: info =>
        {
            var page = new PageData
            {
                Context = new CollectionDownloadPageContext
                {
                    TargetLoadout = targetLoadout,
                    CollectionRevisionMetadataId = _revision,
                },
                FactoryId = CollectionDownloadPageFactory.StaticId,
            };

            var behavior = workspaceController.GetOpenPageBehavior(page, info);
            workspaceController.OpenPage(workspaceId, page, behavior);
        });

        this.WhenActivated(disposables =>
        {
            ImagePipelines.CreateObservable(_collection.Id, tileImagePipeline)
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (bitmap, self) => self.Image = bitmap)
                .AddTo(disposables);

            ImagePipelines.CreateObservable(_collection.Author.Id, userAvatarPipeline)
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (bitmap, self) => self.AuthorAvatar = bitmap)
                .AddTo(disposables);

            var collectionGroupObservable = collectionDownloader.GetCollectionGroupObservable(_revision, targetLoadout);
            collectionDownloader
                .IsCollectionInstalledObservable(_revision, collectionGroupObservable)
                .OnUI()
                .SubscribeWithErrorLogging(isInstalled => IsCollectionInstalled = isInstalled)
                .AddTo(disposables);
        });
    }

    public string Name => _collection.Name;
    [Reactive] public Bitmap? Image { get; private set; }
    [Reactive] public Bitmap? AuthorAvatar { get; private set; }
    public string Summary => _collection.Summary.ValueOr(string.Empty);
    public string Category => _collection.Category.Name;
    public int NumDownloads => _revision.Downloads.Count;
    public RevisionNumber RevisionNumber => _revision.RevisionNumber;
    public ulong EndorsementCount => _collection.Endorsements.ValueOr(0ul);
    public ulong TotalDownloads => _collection.TotalDownloads.ValueOr(0ul);
    public Size TotalSize => _revision.TotalSize.ValueOr(Size.Zero);
    public Percent OverallRating => Percent.Create(_revision.Collection.RecentRating.ValueOr(0), maximum: 100);

    public bool IsAdult => _revision.IsAdult.ValueOr(false);
    public string AuthorName => _collection.Author.Name;
    public ReactiveCommand<NavigationInformation> OpenCollectionDownloadPageCommand { get; }

    [Reactive] public bool IsCollectionInstalled { get; private set; }
}
