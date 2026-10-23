using Avalonia.Platform.Storage;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries.PathsList;

public interface ISettingPathsViewModel : IInteractionControl
{
    ConfigurablePathsContainer ConfigurablePathsContainer { get; }

    R3.ReactiveCommand CommandChangeLocation { get; }
    IStorageProvider? StorageProvider { get; set; }
}
