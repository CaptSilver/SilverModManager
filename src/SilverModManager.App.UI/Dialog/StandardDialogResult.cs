using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.App.UI.Dialog;

public record StandardDialogResult
{
    public ButtonDefinitionId ButtonId { get; set; } = ButtonDefinitionId.None;
    public string InputText { get; set; } = string.Empty;
}
