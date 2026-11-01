using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.CLI.OptionParsers;
using SilverModManager.CLI.Types;
using SilverModManager.CLI.Types.IpcHandlers;
using NexusMods.Paths;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Extension class for <see cref="IServiceCollection"/>
/// </summary>
public static class Services
{
    // ReSharper disable once InconsistentNaming
    /// <summary>
    /// Adds the CLI services to the <see cref="IServiceCollection"/>
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddCLI(this IServiceCollection services)
    {
        // The nxm handler publishes on it even when no window is there to listen.
        services.AddSingleton<IEventBus, EventBus>();

        services.AddOptionParser<AbsolutePath, AbsolutePathParser>()
                .AddOptionParser<IGame, GameParser>()
                .AddOptionParser<Loadout.ReadOnly, LoadoutParser>()
                .AddOptionParser<Uri>(u => (new Uri(u), null))
                .AddOptionParser<Version>(v => (Version.Parse(v), null))
                .AddOptionParser<string>(s => (s, null))
                .AddOptionParser<long>(l => (long.Parse(l), null))
                .AddOptionParser<Matcher, MatcherParser>()
                .AddOptionParser<ITool, ToolParser>();

        // Protocol Handlers
        services.AddSingleton<ManagedGames>();
        services.AddSingleton<IIpcProtocolHandler, NxmIpcProtocolHandler>();
        services.AddSingleton<IIpcProtocolHandler, Ror2mmIpcProtocolHandler>();
        services.AddSettings<Ror2mmHandlerSettings>();
        services.AddSettings<ProtocolNoticeSettings>();
        services.AddSingleton<ProtocolNotices>();

        services.AddProtocolVerbs();
        return services;
    }

    /// <summary>
    /// Claims the schemes the user turned on. Only the process that owns the desktop session adds it: a process
    /// that runs a verb or launches a game must not touch the desktop's link handlers.
    /// </summary>
    public static IServiceCollection AddProtocolRegistration(this IServiceCollection services)
        => services.AddHostedService<ProtocolRegistration>();

}
