using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

public static class ServiceExtensions
{
    public static IServiceCollection AddUISettings(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSettings<LanguageSettings>()
            .AddSettings<TextEditorSettings>()
            .AddSettings<TreeDataGridSortingStateSettings>()
            .AddSettings<AlertSettings>()
            .AddSettings<LoadoutSpineOrderSettings>()
            .AddSettings<MyGamesOrderSettings>()
            .AddSettings<LoadoutCollectionOrderSettings>()
            .AddSettings<StoreSortSettings>()
            .AddSettings<BehaviorSettings>()
            .AddSettings<AdultContentSettings>()
            .AddSettings<UpdaterSettings>()
            .AddSettings<WelcomeSettings>();
    }
}
