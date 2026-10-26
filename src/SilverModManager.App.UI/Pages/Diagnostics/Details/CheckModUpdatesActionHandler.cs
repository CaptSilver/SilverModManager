using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModUpdates;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Asks Nexus whether the mods a regulation merge can't place have newer files, and tells the user which do. A newer
/// file is the likeliest fix, since the author may have rebuilt the regulation for the installed game. Nexus does not
/// say which game build a file targets, so the report says that too.
/// </summary>
[UsedImplicitly]
public sealed class CheckModUpdatesActionHandler(IServiceProvider serviceProvider, IModUpdateService modUpdates) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => VanillaMissingActions.CheckModUpdatesId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var ids = VanillaMissingActions.ParseIds(arguments);
        var db = serviceProvider.GetRequiredService<IConnection>().Db;

        // The user pressed the button, so the app's own cooldown between checks does not apply.
        var status = CacheUpdaterResultStatus.Ok;
        string? failure = null;
        try
        {
            status = (await modUpdates.CheckAndUpdateModPages(CancellationToken.None, notify: true, throttle: false)).ResultStatus;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            failure = exception.Message;
        }

        // The mods are read after the check, which may have refreshed the pages they point at.
        db = serviceProvider.GetRequiredService<IConnection>().Db;
        var mods = ids
            .Select(id => LoadoutItem.Load(db, id))
            .Where(item => item.IsValid())
            .Select(item => (item.Name, ModUpdateReport.Outcome(db, modUpdates, item.LoadoutItemId)))
            .ToArray();
        if (mods.Length == 0) throw new ModsNotFoundException($"None of the mods the diagnostic named is in loadout {loadoutId} any more.");

        await MessageBoxOkViewModel.Show(serviceProvider, "Updates for these mods", ModUpdateReport.Describe(mods, status, failure));
    }
}

/// <summary>
/// The mods a diagnostic pointed at left the loadout before the action ran.
/// </summary>
internal sealed class ModsNotFoundException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The mods were not found";
}
