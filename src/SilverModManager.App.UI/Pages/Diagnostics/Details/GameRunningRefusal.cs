using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// The refusal a Health Check action that changes files gives while the game runs. Each action checks before it asks
/// and again after the answer, since the dialog can sit open while the game starts.
/// </summary>
internal static class GameRunningRefusal
{
    /// <summary>
    /// When the game is running, reports why <paramref name="context"/> (what the action was doing, e.g. "restoring the
    /// backup folder") was refused and returns true; otherwise returns false and reports nothing.
    /// </summary>
    public static async Task<bool> RefuseIfRunning(IServiceProvider serviceProvider, GameInstallation installation, string context)
    {
        if (!serviceProvider.GetRequiredService<IGameLaunchSession>().IsRunning(installation)) return false;

        var refusal = new ExecutableInUseException(string.Format(Language.ErrorGameAlreadyRunning_Description, installation.Game.DisplayName));
        await serviceProvider.GetRequiredService<IErrorPresenter>().Report(refusal, context);
        return true;
    }
}
