using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Wabbajack.Tests;

public class Startup
{
    /// <summary>Every folder the project writes to is under this one in tests.</summary>
    public static readonly string SettingsRoot = $"NexusMods.Wabbajack.Tests-{Guid.NewGuid()}";

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
