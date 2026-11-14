using System.Text;

namespace SilverModManager.Platform.Proton.Tests.Steam;

/// <summary>
/// A <c>shortcuts.vdf</c> built byte by byte with the keys and type bytes Steam writes, so no test depends on a
/// real user's file.
/// </summary>
internal sealed class ShortcutsVdfFixture
{
    private readonly List<byte> _bytes = [];

    public static byte[] Bytes(params (string Key, int AppId, string Name, string Exe)[] children)
    {
        var fixture = new ShortcutsVdfFixture();
        fixture.Map("shortcuts");
        foreach (var (key, appId, name, exe) in children) fixture.Child(key, appId, name, exe);
        fixture.End();
        fixture.End();
        return fixture._bytes.ToArray();
    }

    public static byte[] Bytes() => Bytes(("0", unchecked((int)0x8123_4567), "Game", "\"/usr/bin/game\""));

    /// <summary>The bytes of one child alone, for checking that an untouched child kept its exact bytes.</summary>
    public static byte[] ChildBytes(string key, int appId, string name, string exe)
    {
        var fixture = new ShortcutsVdfFixture();
        fixture.Child(key, appId, name, exe);
        return fixture._bytes.ToArray();
    }

    private void Child(string key, int appId, string name, string exe)
    {
        Map(key);
        Int("appid", appId);
        Str("appname", name);
        Str("exe", exe);
        Str("StartDir", "\"/usr/bin/\"");
        Str("icon", "");
        Str("ShortcutPath", "");
        Str("LaunchOptions", "");
        Int("IsHidden", 0);
        Int("AllowDesktopConfig", 1);
        Int("AllowOverlay", 1);
        Int("OpenVR", 0);
        Int("Devkit", 0);
        Str("DevkitGameID", "");
        Int("DevkitOverrideAppID", 0);
        Int("LastPlayTime", 0);
        Str("FlatpakAppID", "");
        Str("sortas", "");
        Map("tags");
        End();
        End();
    }

    private void Map(string name) { _bytes.Add(0x00); Cstring(name); }
    private void Str(string name, string value) { _bytes.Add(0x01); Cstring(name); Cstring(value); }
    private void Int(string name, int value) { _bytes.Add(0x02); Cstring(name); _bytes.AddRange(BitConverter.GetBytes(value)); }
    private void End() => _bytes.Add(0x08);
    private void Cstring(string text) { _bytes.AddRange(Encoding.UTF8.GetBytes(text)); _bytes.Add(0); }
}
