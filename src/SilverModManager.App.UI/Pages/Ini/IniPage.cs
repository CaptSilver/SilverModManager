using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Ini;

[JsonName("SilverModManager.App.UI.Pages.Ini.IniPageContext")]
public record IniPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class IniPageFactory : APageFactory<IIniPageViewModel, IniPageContext>
{
    private readonly IConnection _conn;

    public IniPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("b2d6a0c4-5f13-4e87-9a3b-61c8d4e07f25"));
    public override PageFactoryId Id => StaticId;

    /// <summary>Whether a game keeps INI files a loadout can own, which is what the page edits.</summary>
    public static bool HasIniPage(object game) => game is IGameIniFiles;

    public override IIniPageViewModel CreateViewModel(IniPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IIniPageViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);
        if (!HasIniPage(loadout.InstallationInstance.Game)) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "INI",
            Icon = IconValues.FileEdit,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new IniPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
