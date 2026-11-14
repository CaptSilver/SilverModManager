using System.Collections.Immutable;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Stops a game left running after an unfinished launch and takes that launch's changes into the loadout, after the
/// user confirms. Not unattended: it ends processes.
/// </summary>
/// <remarks>
/// The diagnostic is not refreshed when the process table changes, so everything is looked at again here: a launch
/// that started since the diagnostic was shown holds the game and must not be signalled.
/// </remarks>
[UsedImplicitly]
public sealed class StopGameAndFinishActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.StopGameAndFinishId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");
        var installation = loadout.InstallationInstance;

        try
        {
            if (serviceProvider.GetRequiredService<IGameLaunchSession>().Describe(installation) is (RunningInThisProcess or HeldByOtherProcess) and { } why)
            {
                await MessageBoxOkViewModel.Show(serviceProvider, "Cannot stop the game", why.Message);
                return;
            }

            var running = serviceProvider.GetRequiredService<IRunningGameDetector>().Running(installation);
            if (running.Game.Count > 0)
            {
                if (!await Confirm(installation.Game.DisplayName, running)) return;

                var survivor = await serviceProvider.GetRequiredService<IGameProcessFactory>().ShutDownGameAsync(installation, null, CancellationToken.None);
                if (survivor is not null)
                {
                    await MessageBoxOkViewModel.Show(serviceProvider, "The game did not stop", $"{survivor.Name} (pid {survivor.Pid}) survived SIGKILL.");
                    return;
                }
            }

            await serviceProvider.GetRequiredService<IPendingLaunchFinisher>().FinishIfPending(installation);
        }
        finally
        {
            await diagnosticManager.RefreshAsync(loadoutId);
        }
    }

    private async Task<bool> Confirm(string gameName, RunningProcesses running)
    {
        var text = new StringBuilder($"{gameName} will be asked to stop, then killed if it does not.{Environment.NewLine}{Environment.NewLine}These processes will be stopped:{Environment.NewLine}");
        foreach (var process in running.Game) text.AppendLine($"{process.Name} (pid {process.Pid})");
        if (running.Tools.Count > 0)
        {
            text.AppendLine().AppendLine("Left alone:");
            foreach (var tool in running.Tools) text.AppendLine($"{tool.Name} (pid {tool.Pid})");
        }

        var dialog = DialogFactory.CreateStandardDialog(
            title: $"Stop {gameName}?",
            new StandardDialogParameters { Text = text.ToString().TrimEnd() },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Stop and finish", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]
        );

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }
}
