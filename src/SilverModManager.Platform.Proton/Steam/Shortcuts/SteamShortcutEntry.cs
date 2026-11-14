using SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// One child of <c>shortcuts.vdf</c>, with every key Steam writes for it.
/// </summary>
/// <param name="Exe">As stored, quoted.</param>
/// <param name="StartDir">As stored, quoted.</param>
public sealed record SteamShortcutEntry(
    uint AppId,
    string AppName,
    string Exe,
    string StartDir,
    string Icon,
    string ShortcutPath = "",
    string LaunchOptions = "",
    bool IsHidden = false,
    bool AllowDesktopConfig = true,
    bool AllowOverlay = true,
    bool OpenVR = false,
    bool Devkit = false,
    string DevkitGameId = "",
    int DevkitOverrideAppId = 0,
    int LastPlayTime = 0,
    string FlatpakAppId = "",
    string SortAs = "",
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>
    /// The name Steam shows for the app's entry.
    /// </summary>
    public const string DisplayName = "SilverModManager";

    /// <summary>
    /// The entry that starts the app through its launcher script.
    /// </summary>
    public static SteamShortcutEntry ForLauncher(string shimPath, string iconPath)
    {
        var exe = Quote(shimPath);
        return new SteamShortcutEntry(SteamShortcutId.AppId(exe, DisplayName), DisplayName, exe, Quote(System.IO.Path.GetDirectoryName(shimPath) ?? "/"), iconPath);
    }

    /// <summary>
    /// How Steam stores a path in <c>exe</c> and <c>StartDir</c>.
    /// </summary>
    public static string Quote(string path) => $"\"{path}\"";

    /// <summary>
    /// The node for this entry under the key <paramref name="childKey"/>, in Steam's key order and types.
    /// </summary>
    public BinaryKvMap ToKv(string childKey) => new(childKey,
    [
        new BinaryKvInt32("appid", unchecked((int)AppId)),
        new BinaryKvString("appname", AppName),
        new BinaryKvString("exe", Exe),
        new BinaryKvString("StartDir", StartDir),
        new BinaryKvString("icon", Icon),
        new BinaryKvString("ShortcutPath", ShortcutPath),
        new BinaryKvString("LaunchOptions", LaunchOptions),
        new BinaryKvInt32("IsHidden", Flag(IsHidden)),
        new BinaryKvInt32("AllowDesktopConfig", Flag(AllowDesktopConfig)),
        new BinaryKvInt32("AllowOverlay", Flag(AllowOverlay)),
        new BinaryKvInt32("OpenVR", Flag(OpenVR)),
        new BinaryKvInt32("Devkit", Flag(Devkit)),
        new BinaryKvString("DevkitGameID", DevkitGameId),
        new BinaryKvInt32("DevkitOverrideAppID", DevkitOverrideAppId),
        new BinaryKvInt32("LastPlayTime", LastPlayTime),
        new BinaryKvString("FlatpakAppID", FlatpakAppId),
        new BinaryKvString("sortas", SortAs),
        new BinaryKvMap("tags", (Tags ?? []).Select((tag, i) => (BinaryKv)new BinaryKvString(i.ToString(), tag)).ToList()),
    ]);

    /// <summary>
    /// Reads an entry from its node. A key Steam omitted reads as its default.
    /// </summary>
    public static SteamShortcutEntry FromKv(BinaryKvMap child)
    {
        string Text(string name) => child.Find(name) is BinaryKvString s ? s.Value : "";
        int Number(string name) => child.Find(name) is BinaryKvInt32 n ? n.Value : 0;
        bool On(string name, bool fallback) => child.Find(name) is BinaryKvInt32 n ? n.Value != 0 : fallback;

        return new SteamShortcutEntry(
            unchecked((uint)Number("appid")), Text("appname"), Text("exe"), Text("StartDir"), Text("icon"), Text("ShortcutPath"), Text("LaunchOptions"),
            On("IsHidden", false), On("AllowDesktopConfig", true), On("AllowOverlay", true), On("OpenVR", false), On("Devkit", false),
            Text("DevkitGameID"), Number("DevkitOverrideAppID"), Number("LastPlayTime"), Text("FlatpakAppID"), Text("sortas"),
            child.Find("tags") is BinaryKvMap tags ? tags.Children.OfType<BinaryKvString>().Select(t => t.Value).ToList() : []);
    }

    private static int Flag(bool value) => value ? 1 : 0;
}
