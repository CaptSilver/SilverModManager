namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The names that have a meaning inside <see cref="NexusMods.Sdk.Games.LocationId.Me3Mods"/>. A mod package is a
/// folder there named after its id, so the folders below that the app uses itself cannot be package ids.
/// </summary>
public static class Me3ModsLayout
{
    /// <summary>Holds one folder per native mod: the archive as shipped, with the DLL me3 loads somewhere in it.</summary>
    public const string NativesFolder = "natives";

    /// <summary>
    /// Holds the files that are placed over the game folder for the length of a session and never written into it.
    /// Its layout mirrors the game folder.
    /// </summary>
    public const string OverlayFolder = "overlay";

    /// <summary>The generated profile, at the root of the mods folder.</summary>
    public const string ProfileFileName = "silver.me3";

    public const string SeamlessCoopFolder = "SeamlessCoop";
    public const string ErscDll = "ersc.dll";
    public const string ErscSettings = "ersc_settings.ini";
    public const string ErscLauncher = "ersc_launcher.exe";

    /// <summary>The Seamless Co-op DLL inside the mods folder, which is where the installer puts it.</summary>
    public static string ErscDllInMods => OverlayPath($"{SeamlessCoopFolder}/{ErscDll}");

    /// <summary>The path inside the mods folder of a file that appears at <paramref name="gameRelativePath"/> in the overlay.</summary>
    public static string OverlayPath(string gameRelativePath) => $"{OverlayFolder}/{gameRelativePath}";

    /// <summary>Whether a package of this id would collide with a folder the app uses itself.</summary>
    public static bool IsReservedPackageId(string id)
        => id.Equals(NativesFolder, StringComparison.OrdinalIgnoreCase) || id.Equals(OverlayFolder, StringComparison.OrdinalIgnoreCase);
}
