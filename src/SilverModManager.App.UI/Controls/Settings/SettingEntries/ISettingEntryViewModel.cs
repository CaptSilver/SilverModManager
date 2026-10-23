using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingEntryViewModel : IViewModelInterface
{
    PropertyConfig Config { get; }

    IMarkdownRendererViewModel DescriptionMarkdownRenderer { get; }

    IInteractionControl InteractionControlViewModel { get; }

    IMarkdownRendererViewModel? LinkRenderer { get; }
}
