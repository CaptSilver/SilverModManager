using SilverModManager.Abstractions.Serialization.ExpressionGenerator;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.About;
using SilverModManager.App.UI.Pages.Changelog;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.DebugControls;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Pages.Downloads;
using SilverModManager.App.UI.Pages.FromSoftMerges;
using SilverModManager.App.UI.Pages.FromSoftSaves;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.Pages.ObservableInfo;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Pages.TextEdit;
using SilverModManager.App.UI.Pages.Tools;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI;

internal class TypeFinder : ITypeFinder
{
    public IEnumerable<Type> DescendentsOf(Type type)
    {
        return AllTypes.Where(t => t.IsAssignableTo(type));
    }

    private static IEnumerable<Type> AllTypes => new[]
    {
        // factory context
        typeof(MyGamesPageContext),
        typeof(DiagnosticListPageContext),
        typeof(ApplyDiffPageContext),
        typeof(SettingsPageContext),
        typeof(ChangelogPageContext),
        typeof(AboutPageContext),
        typeof(TextEditorPageContext),
        typeof(MyLoadoutsPageContext),
        typeof(LoadoutGroupFilesPageContext),
        typeof(LibraryPageContext),
        typeof(LoadoutPageContext),
        typeof(CollectionLoadoutPageContext),
        typeof(ProtocolRegistrationTestPageContext),
        typeof(DownloadsPageContext),
        typeof(ToolsPageContext),
        typeof(FromSoftMergesPageContext),
        typeof(FromSoftSavesPageContext),
        typeof(ThunderstorePageContext),
        typeof(NexusModsPageContext),
        typeof(WabbajackModlistPageContext),
        typeof(WabbajackGalleryPageContext),

        // workspace context
        typeof(EmptyContext),
        typeof(HomeContext),
        typeof(LoadoutContext),
        typeof(DownloadsContext),
        typeof(CollectionDownloadPageContext),
        typeof(ObservableInfoPageContext),
        typeof(DebugControlsPageContext),

        // other
        typeof(WindowData),
    };
}
