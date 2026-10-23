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

    /// <summary>
    /// The folder a list's tools, and the Mod Organizer paths its config files point at, live in. Named after the
    /// list with anything that is not valid in a folder name replaced.
    /// </summary>
    public AbsolutePath ExtrasFor(string listName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var name = new string(listName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        var folder = ExtrasRoot.Combine(name.Length == 0 ? "modlist" : name);
        folder.CreateDirectory();
        return folder;
    }

    private AbsolutePath Ensure(ConfigurablePath configured)
    {
        var path = configured.ToPath(fileSystem);
        path.CreateDirectory();
        return path;
    }
}
