using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// Client id overrides for testing against a staging OAuth client. The Settings page shows each site as an account
/// row (status and a sign-in or sign-out button) on its client id property; the id itself is not edited there.
/// </summary>
/// <remarks>
/// The ids the app ships with live in <c>ForumEndpoints</c>. A non-empty value here wins over the built-in one.
/// </remarks>
public record ForumAuthSettings : ISettings
{
    public string LoversLabClientId { get; set; } = "";
    public string VectorPlexusClientId { get; set; } = "";

    public static string SettingsId => "SilverModManager.Networking.Forums.ForumAuthSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.LoversLabClientId, Row(ForumSite.LoversLab), AccountButton(ForumSite.LoversLab))
            .ConfigureProperty(x => x.VectorPlexusClientId, Row(ForumSite.VectorPlexus), AccountButton(ForumSite.VectorPlexus));
    }

    private static PropertyOptions<ForumAuthSettings, string> Row(ForumSite site) => new()
    {
        Section = Sections.Accounts,
        DisplayName = site.DisplayName(),
        DescriptionFactory = _ => $"Sign in to download {site.DisplayName()} files that modlists ask for.",
    };

    private static ActionButtonContainerOptions AccountButton(ForumSite site) => new()
    {
        CreateSource = services => new ForumAccountButton(
            services.GetRequiredService<IForumAccounts>(),
            services.GetRequiredService<IForumSignInFlow>(),
            site),
    };
}
