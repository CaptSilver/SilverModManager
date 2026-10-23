using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

[JsonName("SilverModManager.App.UI.Pages.Library.LoadoutPageContext")]
public record LoadoutPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
    
    /// <summary>
    /// If provided, will limit the scope of items shown to the group with the given ID.
    /// </summary>
    public required Optional<CollectionGroupId> GroupScope { get; init; }

    /// <summary>
    /// If provided, will create the page with the given sub-tab selected.
    /// </summary>
    public Optional<LoadoutPageSubTabs> SelectedSubTab { get; init; } = LoadoutPageSubTabs.Mods;
}

[UsedImplicitly]
public class LoadoutPageFactory : APageFactory<ILoadoutViewModel, LoadoutPageContext>
{
    private readonly ISettingsManager _settingsManager;
    private readonly IConnection _connection;

    public LoadoutPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("62fda6ce-e6b7-45d6-936f-a8f325bfc644"));
    public override PageFactoryId Id => StaticId;

    public override ILoadoutViewModel CreateViewModel(LoadoutPageContext context)
    {
        var vm = new LoadoutViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(),
            ServiceProvider,
            context.LoadoutId,
            context.GroupScope,
            context.SelectedSubTab
        );
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;
        
        yield return new PageDiscoveryDetails
        {
            SectionName = "Mods",
            ItemName = Language.LoadoutViewPageTitle,
            Icon = IconValues.FormatAlignJustify,
            PageData = new PageData
            {
                FactoryId = Id,
                Context = new LoadoutPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                    GroupScope = Optional<CollectionGroupId>.None,
                },
            },
        };
    }
}

public enum LoadoutPageSubTabs
{
    Mods = 0,
    Rules = 1,
}
