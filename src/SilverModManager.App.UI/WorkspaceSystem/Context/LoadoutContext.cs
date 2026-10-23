using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.WorkspaceSystem;

[JsonName("SilverModManager.App.UI.WorkspaceSystem.LoadoutContext")]
public record LoadoutContext : IWorkspaceContext
{
    public required LoadoutId LoadoutId { get; init; }

    public bool IsValid(IServiceProvider serviceProvider)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, LoadoutId.Value);
        return loadout.IsValid();
    }
}
