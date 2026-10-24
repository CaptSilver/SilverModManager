using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Abstractions.Serialization.ExpressionGenerator;
using SilverModManager.Abstractions.Serialization.Json;
using SilverModManager.App.UI.Controls.DevelopmentBuildBanner;
using SilverModManager.App.UI.Controls.SecretStoreNotice;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.App.UI.Controls.GameWidget;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.MiniGameWidget;
using SilverModManager.App.UI.Controls.MiniGameWidget.ComingSoon;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI.Controls.Settings.Section;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Controls.Settings.SettingEntries.PathsList;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.Controls.Trees.Files;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.LeftMenu.Downloads;
using SilverModManager.App.UI.LeftMenu.Home;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Notifications;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.OkCancel;
using SilverModManager.App.UI.Overlays.LibraryDeleteConfirmation;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.About;
using SilverModManager.App.UI.Pages.Changelog;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.PremiumDownloads;
using SilverModManager.App.UI.Pages.DebugControls;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Pages.Downloads;
using SilverModManager.App.UI.Pages.FromSoftMerges;
using SilverModManager.App.UI.Pages.FromSoftSaves;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.LoadoutPage.Dialogs;
using SilverModManager.App.UI.Pages.LoadoutPage.Dialogs.CollectionPublished;
using SilverModManager.App.UI.Pages.LoadoutPage.Dialogs.ShareCollection;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Pages.ObservableInfo;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Pages.SteamLaunchOption;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Pages.TextEdit;
using SilverModManager.App.UI.Pages.Tools;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Paths;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;
using ImageButton = SilverModManager.App.UI.Controls.Spine.Buttons.Image.ImageButton;
using NexusLoginOverlayView = SilverModManager.App.UI.Overlays.Login.NexusLoginOverlayView;
using SettingToggleControl = SilverModManager.App.UI.Controls.Settings.SettingEntries.SettingToggleControl;

namespace SilverModManager.App.UI;

public static class Services
{
    // ReSharper disable once InconsistentNaming
    public static IServiceCollection AddUI(this IServiceCollection c)
    {
        return c
            // JSON converters
            .AddSingleton<JsonConverter, RectJsonConverter>()
            .AddSingleton<JsonConverter, ColorJsonConverter>()
            .AddSingleton<JsonConverter, AbstractClassConverterFactory<IPageFactoryContext>>()
            .AddSingleton<JsonConverter, AbstractClassConverterFactory<IWorkspaceContext>>()

            // Type Finder
            .AddSingleton<ITypeFinder, TypeFinder>()
            .AddSingleton<GameRunningTracker>()
            .AddTransient<MainWindow>()

            // Services
            .AddSingleton<IOverlayController, OverlayController>()
            .AddSingleton<IApiKeyPrompt, ApiKeyPrompt>()
            .AddSingleton<IWindowNotificationService, WindowNotificationService>()

            // View Models
            .AddTransient<MainWindowViewModel>()
            .AddSingleton<IViewLocator, InjectedViewLocator>()
            
            .AddViewModel<CollectionCardDesignViewModel, ICollectionCardViewModel>()

            .AddViewModel<DevelopmentBuildBannerViewModel, IDevelopmentBuildBannerViewModel>()
            .AddViewModel<SecretStoreNoticeViewModel, ISecretStoreNoticeViewModel>()
            .AddViewModel<GameWidgetViewModel, IGameWidgetViewModel>()
            .AddViewModel<MiniGameWidgetViewModel, IMiniGameWidgetViewModel>()
            .AddViewModel<ComingSoonMiniGameWidgetViewModel, IComingSoonMiniGameWidgetViewModel>()
            .AddViewModel<HomeLeftMenuViewModel, IHomeLeftMenuViewModel>()
            .AddViewModel<IconButtonViewModel, IIconButtonViewModel>()
            .AddViewModel<LeftMenuItemViewModel, ILeftMenuItemViewModel>()
            .AddViewModel<CollectionLeftMenuItemViewModel, ILeftMenuItemViewModel>()
            .AddViewModel<ImageButtonViewModel, IImageButtonViewModel>()
            .AddViewModel<LaunchButtonViewModel, ILaunchButtonViewModel>()
            .AddViewModel<ApplyControlViewModel, IApplyControlViewModel>()
            .AddViewModel<MyGamesViewModel, IMyGamesViewModel>()
            .AddViewModel<NexusLoginOverlayViewModel, INexusLoginOverlayViewModel>()
            .AddViewModel<ApiKeyLoginOverlayViewModel, IApiKeyLoginOverlayViewModel>()
            .AddViewModel<SpineViewModel, ISpineViewModel>()
            .AddViewModel<TopBarViewModel, ITopBarViewModel>()
            .AddViewModel<SpineDownloadButtonViewModel, ISpineDownloadButtonViewModel>()
            .AddViewModel<MessageBoxOkViewModel, IMessageBoxOkViewModel>()
            .AddViewModel<MessageBoxOkCancelViewModel, IMessageBoxOkCancelViewModel>()
            .AddViewModel<UpdaterViewModel, IUpdaterViewModel>()
            .AddViewModel<LoadoutLeftMenuViewModel, ILoadoutLeftMenuViewModel>()
            .AddViewModel<DownloadsLeftMenuViewModel, IDownloadsLeftMenuViewModel>()
            .AddViewModel<FileTreeNodeViewModel, IFileTreeNodeViewModel>()
            .AddViewModel<ApplyDiffViewModel, IApplyDiffViewModel>()

            // Views
            .AddView<CollectionCardView, ICollectionCardViewModel>()
            .AddView<DevelopmentBuildBannerView, IDevelopmentBuildBannerViewModel>()
            .AddView<SecretStoreNoticeView, ISecretStoreNoticeViewModel>()
            .AddView<GameWidget, IGameWidgetViewModel>()
            .AddView<MiniGameWidget, IMiniGameWidgetViewModel>()
            .AddView<ComingSoonMiniGameWidget, IComingSoonMiniGameWidgetViewModel>()
            .AddView<HomeLeftMenuView, IHomeLeftMenuViewModel>()
            .AddView<IconButton, IIconButtonViewModel>()
            .AddView<LeftMenuItemView, ILeftMenuItemViewModel>()
            .AddView<ImageButton, IImageButtonViewModel>()
            .AddView<LaunchButtonView, ILaunchButtonViewModel>()
            .AddView<MyGamesView, IMyGamesViewModel>()
            .AddView<NexusLoginOverlayView, INexusLoginOverlayViewModel>()
            .AddView<ApiKeyLoginOverlayView, IApiKeyLoginOverlayViewModel>()
            .AddView<Spine, ISpineViewModel>()
            .AddView<TopBarView, ITopBarViewModel>()
            .AddView<SpineDownloadButtonView, ISpineDownloadButtonViewModel>()
            .AddView<MessageBoxOkView, IMessageBoxOkViewModel>()
            .AddView<SteamLaunchOptionView, ISteamLaunchOptionViewModel>()
            .AddView<MessageBoxOkCancelView, IMessageBoxOkCancelViewModel>()
            .AddView<UpdaterView, IUpdaterViewModel>()
            .AddView<LoadoutLeftMenuView, ILoadoutLeftMenuViewModel>()
            .AddView<DownloadsLeftMenuView, IDownloadsLeftMenuViewModel>()
            .AddView<ApplyControlView, IApplyControlViewModel>()
            .AddView<FileTreeNodeView, IFileTreeNodeViewModel>()
            .AddView<ApplyDiffView, IApplyDiffViewModel>()
            .AddView<FileTreeView, IFileTreeViewModel>()
            
            
            .AddView<MyLoadoutsView, IMyLoadoutsViewModel>()
            .AddViewModel<MyLoadoutsViewModel, IMyLoadoutsViewModel>()
            .AddView<LoadoutCardView, ILoadoutCardViewModel>()
            .AddView<CreateNewLoadoutCardView, ICreateNewLoadoutCardViewModel>()
            .AddViewModel<LoadoutBadgeViewModel, ILoadoutBadgeViewModel>()
            
            .AddView<SettingsView, ISettingsPageViewModel>()
            .AddViewModel<SettingsPageViewModel, ISettingsPageViewModel>()

            .AddView<SettingSectionView, ISettingSectionViewModel>()
            .AddViewModel<SettingSectionViewModel, ISettingSectionViewModel>()

            .AddView<SettingEntryView, ISettingEntryViewModel>()
            .AddViewModel<SettingEntryViewModel, ISettingEntryViewModel>()
            .AddView<SettingToggleControl, ISettingToggleViewModel>()
            .AddViewModel<SettingToggleViewModel, ISettingToggleViewModel>()
            .AddView<SettingComboBoxView, ISettingComboBoxViewModel>()
            .AddViewModel<SettingComboBoxViewModel, ISettingComboBoxViewModel>()
            .AddView<SettingPathsControl, ISettingPathsViewModel>()
            .AddViewModel<SettingPathsViewModel, ISettingPathsViewModel>()

            .AddView<DiagnosticEntryView, IDiagnosticEntryViewModel>()
            .AddViewModel<DiagnosticEntryViewModel, IDiagnosticEntryViewModel>()
            .AddView<DiagnosticListView, IDiagnosticListViewModel>()
            .AddViewModel<DiagnosticListViewModel, IDiagnosticListViewModel>()
            .AddView<DiagnosticDetailsView, IDiagnosticDetailsViewModel>()
            .AddViewModel<DiagnosticDetailsViewModel, IDiagnosticDetailsViewModel>()

            .AddView<MarkdownRendererView, IMarkdownRendererViewModel>()
            .AddViewModel<MarkdownRendererViewModel, IMarkdownRendererViewModel>()
            .AddView<ChangelogPageView, IChangelogPageViewModel>()
            .AddViewModel<ChangelogPageViewModel, IChangelogPageViewModel>()
            .AddView<ThunderstoreView, IThunderstoreViewModel>()
            .AddViewModel<ThunderstoreViewModel, IThunderstoreViewModel>()
            .AddSingleton<IThunderstoreIconLoader>(serviceProvider => new ThunderstoreIconLoader(
                ImagePipelines.GetThunderstoreIconPipeline(serviceProvider),
                ImagePipelines.ModPageThumbnailFallback,
                logger: serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThunderstoreIconLoader>>()))
            .AddView<WabbajackModlistView, IWabbajackModlistViewModel>()
            .AddViewModel<WabbajackModlistViewModel, IWabbajackModlistViewModel>()
            .AddSingleton<IWabbajackModlistService, WabbajackModlistService>()
            .AddView<FromSoftMergesView, IFromSoftMergesViewModel>()
            .AddViewModel<FromSoftMergesViewModel, IFromSoftMergesViewModel>()
            .AddView<FromSoftSavesView, IFromSoftSavesViewModel>()
            .AddViewModel<FromSoftSavesViewModel, IFromSoftSavesViewModel>()
            .AddView<ToolsPageView, IToolsPageViewModel>()
            .AddViewModel<ToolsPageViewModel, IToolsPageViewModel>()
            .AddView<AboutPageView, IAboutPageViewModel>()
            .AddViewModel<AboutPageViewModel, IAboutPageViewModel>()

            .AddView<TextEditorPageView, ITextEditorPageViewModel>()
            .AddViewModel<TextEditorPageViewModel, ITextEditorPageViewModel>()

            .AddView<LibraryItemDeleteConfirmationView, ILibraryItemDeleteConfirmationViewModel>()
            .AddViewModel<LibraryItemDeleteConfirmationViewModel, ILibraryItemDeleteConfirmationViewModel>()

            .AddView<LibraryView, ILibraryViewModel>()
            .AddView<DownloadsPageView, IDownloadsPageViewModel>()
            .AddView<LoadoutView, ILoadoutViewModel>()

            .AddView<CollectionDownloadView, ICollectionDownloadViewModel>()
            .AddViewModel<CollectionDownloadViewModel, ICollectionDownloadViewModel>()
            
            .AddView<LoadOrderView, ILoadOrderViewModel>()

            .AddView<UpgradeToPremiumView, IUpgradeToPremiumViewModel>()
            .AddViewModel<UpgradeToPremiumViewModel, IUpgradeToPremiumViewModel>()

            .AddView<CollectionLoadoutView, ICollectionLoadoutViewModel>()
            .AddViewModel<CollectionLoadoutViewModel, ICollectionLoadoutViewModel>()

            .AddView<ObservableInfoPageView, IObservableInfoPageViewModel>()
            .AddViewModel<ObservableInfoPageViewModel, IObservableInfoPageViewModel>()
            
            .AddView<DebugControlsPageView, IDebugControlsPageViewModel>()
            .AddViewModel<DebugControlsPageViewModel, IDebugControlsPageViewModel>()

            .AddView<ManualDownloadRequiredOverlayView, IManualDownloadRequiredOverlayViewModel>()
            .AddViewModel<ManualDownloadRequiredOverlayViewModel, IManualDownloadRequiredOverlayViewModel>()

            .AddView<RemoveGameOverlayView, IRemoveGameOverlayViewModel>()
            .AddViewModel<RemoveGameOverlayViewModel, IRemoveGameOverlayViewModel>()

            .AddView<WelcomeOverlayView, IWelcomeOverlayViewModel>()
            .AddViewModel<WelcomeOverlayViewModel, IWelcomeOverlayViewModel>()
            
            // Dialogs
            .AddView<DialogStandardContentView, IDialogStandardContentViewModel>()
            .AddViewModel<DialogStandardContentViewModel, IDialogStandardContentViewModel>()
            .AddView<DialogShareCollectionView, IDialogShareCollectionViewModel>()
            .AddViewModel<DialogShareCollectionViewModel, IDialogShareCollectionViewModel>()
            .AddView<DialogCollectionPublishedView, IDialogCollectionPublishedViewModel>()
            .AddViewModel<DialogCollectionPublishedViewModel, IDialogCollectionPublishedViewModel>()
            
            .AddView<DialogPremiumCollectionDownloadsView, IDialogPremiumCollectionDownloadsViewModel>()
            .AddViewModel<DialogPremiumCollectionDownloadsViewModel, IDialogPremiumCollectionDownloadsViewModel>()

            .AddView<ProtocolRegistrationTestPageView, IProtocolRegistrationTestPageViewModel>()
            .AddViewModel<ProtocolRegistrationTestPageViewModel, IProtocolRegistrationTestPageViewModel>()

            .AddView<LoadoutGroupFilesView, ILoadoutGroupFilesViewModel>()
            .AddViewModel<LoadoutGroupFilesViewModel, ILoadoutGroupFilesViewModel>()

            // workspace system
            .AddSingleton<IWindowManager, WindowManager>()
            .AddWindowDataAttributesModel()
            .AddViewModel<WorkspaceViewModel, IWorkspaceViewModel>()
            .AddViewModel<PanelViewModel, IPanelViewModel>()
            .AddViewModel<AddPanelButtonViewModel, IAddPanelButtonViewModel>()
            .AddViewModel<AddPanelDropDownViewModel, IAddPanelDropDownViewModel>()
            .AddViewModel<PanelTabHeaderViewModel, IPanelTabHeaderViewModel>()
            .AddViewModel<NewTabPageViewModel, INewTabPageViewModel>()
            .AddViewModel<NewTabPageSectionViewModel, INewTabPageSectionViewModel>()
            .AddView<WorkspaceView, IWorkspaceViewModel>()
            .AddView<PanelView, IPanelViewModel>()
            .AddView<AddPanelButtonView, IAddPanelButtonViewModel>()
            .AddView<AddPanelDropDownView, IAddPanelDropDownViewModel>()
            .AddView<PanelTabHeaderView, IPanelTabHeaderViewModel>()
            .AddView<NewTabPageView, INewTabPageViewModel>()

            // page factories
            .AddSingleton<PageFactoryController>()
            .AddSingleton<IPageFactory, NewTabPageFactory>()
            .AddSingleton<IPageFactory, MyGamesPageFactory>()
            .AddSingleton<IPageFactory, DiagnosticListPageFactory>()
            .AddSingleton<IPageFactory, DiagnosticDetailsPageFactory>()
            .AddDiagnosticActionHandlers()
            .AddSingleton<IPageFactory, ApplyDiffPageFactory>()
            .AddSingleton<IPageFactory, SettingsPageFactory>()
            .AddSingleton<IPageFactory, ChangelogPageFactory>()
            .AddSingleton<IPageFactory, FromSoftMergesPageFactory>()
            .AddSingleton<IPageFactory, FromSoftSavesPageFactory>()
            .AddSingleton<IPageFactory, ThunderstorePageFactory>()
            .AddSingleton<IPageFactory, WabbajackModlistPageFactory>()
            .AddSingleton<IPageFactory, ToolsPageFactory>()
            .AddSingleton<IPageFactory, AboutPageFactory>()
            .AddSingleton<IPageFactory, TextEditorPageFactory>()
            .AddSingleton<IPageFactory, MyLoadoutsPageFactory>()
            .AddSingleton<IPageFactory, LibraryPageFactory>()
            .AddSingleton<IPageFactory, DownloadsPageFactory>()
            .AddSingleton<IPageFactory, LoadoutPageFactory>()
            .AddSingleton<IPageFactory, CollectionDownloadPageFactory>()
            .AddSingleton<IPageFactory, CollectionLoadoutPageFactory>()
            .AddSingleton<IPageFactory, ObservableInfoPageFactory>()
            .AddSingleton<IPageFactory, DebugControlsPageFactory>()
            .AddSingleton<IPageFactory, ProtocolRegistrationTestPageFactory>()
            .AddSingleton<IPageFactory, LoadoutGroupFilesPageFactory>()

            // LeftMenu factories
            .AddSingleton<ILeftMenuFactory, HomeLeftMenuFactory>()
            .AddSingleton<ILeftMenuFactory, LoadoutLeftMenuFactory>()
            .AddSingleton<ILeftMenuFactory, DownloadsLeftMenuFactory>()

            // Workspace Attachments
            .AddSingleton<IWorkspaceAttachmentsFactoryManager, WorkspaceAttachmentsFactoryManager>()
            .AddSingleton<IWorkspaceAttachmentsFactory, DownloadsAttachmentsFactory>()
            .AddSingleton<IWorkspaceAttachmentsFactory, HomeAttachmentsFactory>()
            .AddSingleton<IWorkspaceAttachmentsFactory, LoadoutAttachmentsFactory>()

            // Diagnostics
            .AddDiagnosticWriter()

            // Overlay Helpers
            .AddHostedService<NexusLoginOverlayService>()
            .AddHostedService<OpenUriFailureService>()

            // Settings
            .AddUISettings()
            .AddSingleton<IInteractionControlFactory<SingleValueMultipleChoiceContainerOptions>, SettingComboBoxFactory>()
            .AddSingleton<IInteractionControlFactory<BooleanContainerOptions>, SettingToggleFactory>()
            .AddSingleton<IInteractionControlFactory<ConfigurablePathsContainerOption>, SettingPathsFactory>()

            // Other
            .AddSingleton<InjectedViewLocator>()
            .AddSingleton<CollectionDataProvider>()
            .AddSingleton<ILibraryDataProvider, LocalFileDataProvider>()
            .AddSingleton<ILoadoutDataProvider, LocalFileDataProvider>()
            .AddSingleton<ILibraryDataProvider, ManuallyCreatedArchiveDataProvider>()
            .AddSingleton<ILoadoutDataProvider, ManuallyCreatedArchiveDataProvider>()
            .AddSingleton<ILibraryDataProvider, NexusModsDataProvider>()
            .AddSingleton<ILoadoutDataProvider, NexusModsDataProvider>()
            .AddSingleton<ILibraryDataProvider, ThunderstoreDataProvider>()
            .AddSingleton<ILibraryDataProvider, WabbajackModlistDataProvider>()
            .AddSingleton<ILoadoutDataProvider, ThunderstoreDataProvider>()
            .AddSingleton<ILoadoutDataProvider, BundledDataProvider>()
            .AddSingleton<ILoadoutDataProvider, GameInstalledDataProvider>()
            .AddSingleton<ILoadOrderDataProvider, LoadOrderDataProvider>()
            .AddSingleton<IDownloadsDataProvider, DownloadsDataProvider>()
            .AddSingleton<IAvaloniaInterop, AvaloniaInterop>()
            .AddSingleton<UpdateChecker>()
            .AddFileSystem()
            .AddImagePipelines();
        
        
    }

}
