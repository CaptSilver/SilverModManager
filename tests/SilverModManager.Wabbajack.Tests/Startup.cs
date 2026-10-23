using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Wabbajack.Tests;

public class Startup
{
    /// <summary>Every folder the project writes to is under this one in tests.</summary>
    public static readonly string SettingsRoot = $"SilverModManager.Wabbajack.Tests-{Guid.NewGuid()}";

    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddDefaultServicesForTesting()
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .AddWabbajack()
            .OverrideSettingsForTests<WabbajackSettings>(settings => settings with
            {
                InboxPath = new ConfigurablePath(KnownPath.EntryDirectory, $"{SettingsRoot}/Inbox"),
                ExtrasRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"{SettingsRoot}/Extras"),
            });
    }
}
