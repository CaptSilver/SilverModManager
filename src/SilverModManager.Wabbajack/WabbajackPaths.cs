using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack;

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

    /// <summary>The folder the cached gallery listing is written to.</summary>
    public AbsolutePath GalleryCache => Ensure(settingsManager.Get<WabbajackSettings>().GalleryCachePath);

    /// <summary>
    /// The folder a list's tools, and the Mod Organizer paths its config files point at, live in. Named after the
    /// list with anything that is not valid in a folder name replaced, and ending in the group's id: two lists can
    /// sanitise to the same name, and removing one must not touch the other's folder.
    /// </summary>
    public AbsolutePath ExtrasFor(string listName, WabbajackModlistLoadoutGroupId group)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var name = new string(listName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        var folder = ExtrasRoot.Combine($"{(name.Length == 0 ? "modlist" : name)}-{group.Value:x}");
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
