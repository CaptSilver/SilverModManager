using System.Collections.Immutable;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Switches off the mods a regulation merge can't place, after the user has seen their names, so the rest of the
/// loadout can apply. The files stay where they are and the mods can be switched back on from the loadout page. A
/// mod whose switch belongs to a collection or to the game's own install is left on and named in the confirmation,
/// the same rule the loadout page's switch follows.
/// </summary>
[UsedImplicitly]
public sealed class DisableModsActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => VanillaMissingActions.DisableModsId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var ids = VanillaMissingActions.ParseIds(arguments);
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var present = ids.Select(id => LoadoutItem.Load(connection.Db, id)).Where(item => item.IsValid() && !item.IsDisabled).ToArray();
        var left = present.Where(LoadoutDataProviderHelper.IsToggleLocked).ToArray();
        var disabling = present.Except(left).ToArray();

        if (disabling.Length == 0)
        {
            if (left.Length > 0)
                await MessageBoxOkViewModel.Show(serviceProvider, "These mods can't be switched off here", ConfirmationText([], left.Select(m => m.Name).ToArray()));
            return;
        }

        if (!await Confirm(ConfirmationText(disabling.Select(m => m.Name).ToArray(), left.Select(m => m.Name).ToArray()))) return;

        using var tx = connection.BeginTransaction();
        foreach (var mod in disabling) tx.Add(mod.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();

        await diagnosticManager.RefreshAsync(loadoutId);
    }

    /// <summary>The list the user confirms: the mods that go off, then the ones that can't.</summary>
    public static string ConfirmationText(IReadOnlyList<string> disabling, IReadOnlyList<string> left)
    {
        var text = new StringBuilder();
        if (disabling.Count > 0)
        {
            text.AppendLine("These mods are switched off in this loadout. Their files are kept and you can switch them back on from the loadout page.").AppendLine();
            foreach (var name in disabling) text.Append("- ").AppendLine(name);
        }

        if (left.Count > 0)
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine(disabling.Count > 0
                ? "These stay on, because a collection or the game's install controls their switch:"
                : "A collection or the game's install controls the switch of these mods, so they can't be turned off here:").AppendLine();
            foreach (var name in left) text.Append("- ").AppendLine(name);
        }

        return text.ToString().TrimEnd();
    }

    private async Task<bool> Confirm(string text)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            title: "Switch these mods off?",
            new StandardDialogParameters { Text = text },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Switch off", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]);

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }
}
