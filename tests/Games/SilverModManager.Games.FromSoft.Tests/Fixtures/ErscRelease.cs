using System.IO.Compression;
using System.Text;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// The shape of a Seamless Co-op release archive. The installer and the profile both name its members by path, so
/// one place holds the archive and a test checks the production paths against it.
/// </summary>
internal static class ErscRelease
{
    public const string Launcher = "ersc_launcher.exe";
    public const string Dll = "SeamlessCoop/ersc.dll";
    public const string Settings = "SeamlessCoop/ersc_settings.ini";

    /// <summary>The settings file as shipped: no password, and the save extension that keeps co-op saves apart.</summary>
    public const string SettingsText = "[PASSWORD]\ncooppassword = \n[SAVE]\nsave_file_extension = co2\n";

    public static async Task Write(AbsolutePath path, string? folderInsideArchive = null)
    {
        var prefix = folderInsideArchive is null ? "" : folderInsideArchive + "/";
        await using var stream = path.Create();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in new[] { (Launcher, "launcher"), (Dll, "dll"), (Settings, SettingsText) })
        {
            await using var entry = zip.CreateEntry(prefix + name).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content));
        }
    }

    public static string[] Members(AbsolutePath path)
    {
        using var stream = path.Read();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }
}
