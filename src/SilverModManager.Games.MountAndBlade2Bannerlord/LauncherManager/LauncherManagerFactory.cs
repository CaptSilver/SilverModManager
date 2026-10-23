using System.Collections.Concurrent;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.MountAndBlade2Bannerlord.Utils;
using NexusMods.Sdk.Games;
using GameInstallMetadata = NexusMods.Sdk.Games.GameInstallMetadata;

namespace NexusMods.Games.MountAndBlade2Bannerlord.LauncherManager;

/// <summary>
/// The LauncherManager holds a state per game instance.
///
/// (Aragas)
/// This could be replaced by introducing a game instance DI scope.
/// In that case the LauncherManager could be a scoped service and this factory would not be needed.
/// </summary>
public sealed class LauncherManagerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, LauncherManagerSilverModManager> _instances = new();

    public LauncherManagerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public LauncherManagerSilverModManager Get(GameInstallMetadata.ReadOnly installation)
    {
        var store = Converter.ToGameStoreTW(installation.Store);
        return _instances.GetOrAdd(installation.Path,
            static (installationPath, tuple) => ValueFactory(tuple._serviceProvider, installationPath, tuple.store), (_serviceProvider, store));
    }

    public LauncherManagerSilverModManager Get(GameInstallation installation)
    {
        var store = Converter.ToGameStoreTW(installation.LocatorResult.Store);
        return _instances.GetOrAdd(installation.Locations[LocationId.Game].Path.ToString(),
            static (installationPath, tuple) => ValueFactory(tuple._serviceProvider, installationPath, tuple.store), (_serviceProvider, store));
    }

    public LauncherManagerSilverModManager Get(GameLocatorResult gameLocator)
    {
        var store = Converter.ToGameStoreTW(gameLocator.Store);
        return _instances.GetOrAdd(gameLocator.Path.ToString(),
            static (installationPath, tuple) => ValueFactory(tuple._serviceProvider, installationPath, tuple.store), (_serviceProvider, store));
    }

    public LauncherManagerSilverModManager Get(string installationPath, global::Bannerlord.LauncherManager.Models.GameStore store)
    {
        return _instances.GetOrAdd(installationPath,
            static (installationPath, tuple) => ValueFactory(tuple._serviceProvider, installationPath, tuple.store), (_serviceProvider, store));
    }

    private static LauncherManagerSilverModManager ValueFactory(IServiceProvider serviceProvider, string installationPath, global::Bannerlord.LauncherManager.Models.GameStore store)
    {
        return new LauncherManagerSilverModManager(serviceProvider, installationPath, store);
    }
}
