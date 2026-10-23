using Avalonia.Controls;
using SilverModManager.App.UI.Dialog.Enums;

namespace SilverModManager.App.UI.Dialog;

public interface IDialog
{
    public Task<StandardDialogResult> Show(Window owner, bool isModal = true);
}
