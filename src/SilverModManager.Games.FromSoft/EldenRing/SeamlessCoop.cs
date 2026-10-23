using NexusMods.Games.FromSoft.Me3;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>Seamless Co-op, the multiplayer mod Elden Ring has and the other FromSoft games do not.</summary>
public static class SeamlessCoop
{
    public const string Folder = "SeamlessCoop";
    public const string Dll = "ersc.dll";
    public const string Settings = "ersc_settings.ini";
    public const string Launcher = "ersc_launcher.exe";

    /// <summary>The Seamless Co-op DLL inside the mods folder, which is where the installer puts it.</summary>
    public static string DllInMods => Me3ModsLayout.OverlayPath($"{Folder}/{Dll}");

    /// <summary>The DLL me3 injects. It writes its own save, so a profile that loads it points at no other.</summary>
    public static readonly Me3OverlayNative Native = new(DllInMods, $"{Folder}/{Dll}", OwnsSave: true);
}
