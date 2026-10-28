using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Witcher.Witcher1;
using SilverModManager.Games.Witcher.Witcher2;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Games.Witcher;

public static class Services
{
    public static IServiceCollection AddWitcherGames(this IServiceCollection services)
    {
        return services
            .AddSingleton<SkippedScriptMergerEmitter>()
            .AddSingleton<UnplacedArchiveFilesEmitter>()
            .AddSingleton<ModsSettingsReplacedEmitter>()
            .AddSingleton<DroppedSettingsPartEmitter>()
            .AddSingleton<MenuXmlOverwriteEmitter>()
            .AddWitcherModsSettingsNoticeModel()
            .AddWitcherModSortOrderModel()
            .AddWitcherModSortOrderItemModel()
            .AddGame<Witcher1Game>()
            .AddGame<Witcher2Game>()
            .AddModule("witcher3", "Commands for The Witcher 3")
            .AddVerb(() => Witcher3Verbs.ModsSettings)
            .AddGame<Witcher3Game>();
    }
}
