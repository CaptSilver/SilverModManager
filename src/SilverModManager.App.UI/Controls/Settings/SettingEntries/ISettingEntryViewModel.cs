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

    /// <summary>Why the setting can't be changed right now, or null when it can. Fixed when the entry is created.</summary>
    string? DisabledReason { get; }

    /// <summary>True exactly when <see cref="DisabledReason"/> is null.</summary>
    bool IsEnabled { get; }
}
