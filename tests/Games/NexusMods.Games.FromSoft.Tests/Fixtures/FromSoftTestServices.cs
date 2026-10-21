using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

public static class FromSoftTestServices
{
    /// <summary>
    /// Registers the FromSoft games with a data root of their own. The default root is one folder under
    /// XDG_DATA_HOME for every test host in the run, so parallel tests would share one Me3Mods folder and a sync in
    /// one test would back up files that another test is deleting.
    /// </summary>
    public static IServiceCollection AddFromSoftGamesWithOwnDataRoot(this IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"FromSoft.{Guid.NewGuid():N}");
        return services
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot });
    }
}
