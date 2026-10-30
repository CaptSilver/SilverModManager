using R3;
using SilverModManager.Networking.Forums;

namespace SilverModManager.App.UI.Pages.Wabbajack;

internal static class ForumAccountsExtensions
{
    /// <summary>The sites whose account changes, for pages that observe with System.Reactive.</summary>
    public static IObservable<ForumSite> ChangedSites(this IForumAccounts accounts) => accounts.Changed.AsSystemObservable();
}
