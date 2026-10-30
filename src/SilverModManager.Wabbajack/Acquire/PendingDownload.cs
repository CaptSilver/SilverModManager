using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// The modlist whose download batch is under way, kept in the settings so that a restart can pick it up again. It
/// follows the acquirer's run: set when a run starts, cleared when it ends or is cancelled, and left alone when the
/// app closes under it.
/// </summary>
public static class PendingDownload
{
    public static LibraryFileId? Read(ISettingsManager settings)
        => settings.Get<WabbajackSettings>() is { } current && EntityId.TryParseFromHex(current.PendingDownload, out var id)
            ? LibraryFileId.From(id)
            : null;

    public static void Set(ISettingsManager settings, LibraryFileId? modlist)
    {
        var text = modlist is { } id ? id.Value.Value.ToString("X") : "";
        if (settings.Get<WabbajackSettings>() is { } current && current.PendingDownload == text) return;
        settings.Update<WabbajackSettings>(s => s with { PendingDownload = text });
    }
}
