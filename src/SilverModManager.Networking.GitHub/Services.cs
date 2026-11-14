using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// Service registration.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers an implementation of <see cref="IGitHubApi"/>.
    /// </summary>
    public static IServiceCollection AddGitHubApi(this IServiceCollection serviceCollection)
    {
        // The window registers its overlay prompt before this runs; a host without one refuses and names the row.
        serviceCollection.TryAddSingleton<ISecretPrompt, HeadlessSecretPrompt>();
        return serviceCollection
            .AddSecretKeys(GitHubSecretKeys.All)
            .AddSettings<GitHubAuthSettings>()
            .AddSingleton<IGitHubApi, GitHubApi>()
            .AddSingleton<GitHubAccount>()
            .AddSingleton<ISecretPrimable>(sp => sp.GetRequiredService<GitHubAccount>());
    }
}
