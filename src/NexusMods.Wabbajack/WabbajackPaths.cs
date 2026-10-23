using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Wabbajack;

/// <summary>
/// The folders Wabbajack support writes to, resolved from <see cref="WabbajackSettings"/> and created when first
/// asked for. Everything the project writes outside the loadout and temp goes through here.
/// </summary>
public sealed class WabbajackPaths(ISettingsManager settingsManager, IFileSystem fileSystem)
{
    /// <summary>Where browser downloads are picked up from.</summary>
    public AbsolutePath Inbox => Ensure(settingsManager.Get<WabbajackSettings>().InboxPath);

    /// <summary>The parent of the per-installation extras folders.</summary>
    public AbsolutePath ExtrasRoot => Ensure(settingsManager.Get<WabbajackSettings>().ExtrasRoot);

    private AbsolutePath Ensure(ConfigurablePath configured)
    {
        var path = configured.ToPath(fileSystem);
        path.CreateDirectory();
        return path;
    }
}
