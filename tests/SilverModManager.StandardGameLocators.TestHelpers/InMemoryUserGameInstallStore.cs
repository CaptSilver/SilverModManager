using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Games;

namespace NexusMods.StandardGameLocators.TestHelpers;

/// <summary>
/// Holds user installs for one test host. Test hosts never use the YAML store: it writes a file under the
/// config folder, which every test host in the run would share.
/// </summary>
public sealed class InMemoryUserGameInstallStore : IUserGameInstallStore
{
    private readonly List<UserGameInstall> _installs = [];

    public IReadOnlyList<UserGameInstall> GetAll()
    {
        lock (_installs) return _installs.ToArray();
    }

    public void Add(UserGameInstall install)
    {
        lock (_installs) _installs.Add(install);
    }
}

public static class InMemoryUserGameInstallStoreExtensions
{
    /// <summary>
    /// Registers one <see cref="InMemoryUserGameInstallStore"/> for the container.
    /// </summary>
    public static IServiceCollection AddInMemoryUserGameInstallStore(this IServiceCollection services)
        => services.AddSingleton<IUserGameInstallStore, InMemoryUserGameInstallStore>();
}
