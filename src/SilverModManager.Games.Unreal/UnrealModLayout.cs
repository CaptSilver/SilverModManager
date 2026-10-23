using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// Where the mods of an Unreal Engine 4/5 game go. A game composes one with its own project folder and asks
/// it about each file of an archive; whatever it does not recognise is left to the game.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Pak mods (.pak with its .utoc and .ucas) go to Paks/~mods. The companions are placed by the same rule as
/// the pak, so they stay in one folder; a subfolder below ~mods or Paks is kept.</item>
/// <item>Blueprint mods, anything under a LogicMods folder, go to Paks/LogicMods.</item>
/// <item>UE4SS mods go to ue4ss/Mods/&lt;ModName&gt; beside the game binary, recognised by a Mods folder at the
/// archive root or by a Scripts (Lua), dlls or enabled.txt entry directly below the mod's folder.</item>
/// <item>The UE4SS loader (dwmapi.dll, UE4SS.dll, UE4SS-settings.ini) goes next to the game binary, or into
/// ue4ss when the archive packs it that way.</item>
/// </list>
/// Paths are matched without regard to case. Both roots are constructor parameters because the project folder
/// name, and the platform folder under Binaries, differ between games.
/// </remarks>
/// <param name="contentRoot">The project's Content folder; Paks lives under it.</param>
/// <param name="binariesRoot">The folder holding the game binary, e.g. Binaries/Win64; ue4ss lives under it.</param>
public sealed class UnrealModLayout(GamePath contentRoot, GamePath binariesRoot)
{
    private const string Ue4ssFolder = "ue4ss";
    private const string ModsFolder = "Mods";

    private static readonly string[] Ue4ssLoaderFiles = ["dwmapi.dll", "UE4SS.dll", "UE4SS-settings.ini"];
    private static readonly string[] PakFamilyExtensions = [".pak", ".utoc", ".ucas"];

    /// <summary>The standard project layout: Content and Binaries/Win64 under the project folder.</summary>
    public static UnrealModLayout ForProject(GamePath projectRoot) => new(projectRoot / "Content", projectRoot / "Binaries/Win64");

    private GamePath ModsRoot => contentRoot / "Paks/~mods";
    private GamePath LogicModsRoot => contentRoot / "Paks/LogicMods";
    private GamePath Ue4ssRoot => binariesRoot / Ue4ssFolder;

    /// <summary>Where the archive file at <paramref name="path"/> belongs, or false when this layout has no rule for it.</summary>
    public bool TryRoute(RelativePath path, out GamePath target)
    {
        var parts = path.Parts.Select(p => p.ToString()).ToArray();
        if (parts.Length == 0)
        {
            target = default;
            return false;
        }

        // Archives packed from the game's own folders: whatever comes below keeps its place.
        if (IndexOfPair(parts, "Binaries", binariesRoot.Path.FileName.ToString()) is var binaries and >= 0)
            return Found(binariesRoot, parts, binaries + 2, out target);

        if (IndexOf(parts, Ue4ssFolder, parts.Length - 1) is var ue4ss and >= 0)
            return Found(Ue4ssRoot, parts, ue4ss + 1, out target);

        // "Mods" is too common a folder name to match at any depth (textures/mods/... is a Bethesda texture);
        // deeper UE4SS mods are found by their Scripts, dlls or enabled.txt instead.
        if (parts.Length > 1 && parts[0].Equals(ModsFolder, StringComparison.OrdinalIgnoreCase))
            return Found(Ue4ssRoot / ModsFolder, parts, 1, out target);

        if (IsUe4ssMod(parts, out var modFolder))
            return Found(Ue4ssRoot / ModsFolder, parts, modFolder, out target);

        if (Ue4ssLoaderFiles.Contains(parts[^1], StringComparer.OrdinalIgnoreCase))
            return Found(binariesRoot, parts, parts.Length - 1, out target);

        if (IndexOf(parts, "LogicMods", parts.Length - 1) is var logic and >= 0)
            return Found(LogicModsRoot, parts, logic + 1, out target);

        var pakFolder = Math.Max(IndexOf(parts, "~mods", parts.Length - 1), IndexOf(parts, "Paks", parts.Length - 1));
        if (pakFolder >= 0)
            return Found(ModsRoot, parts, pakFolder + 1, out target);

        if (PakFamilyExtensions.Contains(Path.GetExtension(parts[^1]), StringComparer.OrdinalIgnoreCase))
            return Found(ModsRoot, parts, parts.Length - 1, out target);

        target = default;
        return false;
    }

    // "<ModName>/Scripts/*.lua", "<ModName>/dlls/*.dll" and "<ModName>/enabled.txt", at any depth. The extension check on
    // Scripts keeps Bethesda's compiled scripts folder from being taken for a mod.
    private static bool IsUe4ssMod(string[] parts, out int modFolder)
    {
        modFolder = -1;
        var file = parts[^1];
        var extension = Path.GetExtension(file);

        if (parts.Length >= 2 && file.Equals("enabled.txt", StringComparison.OrdinalIgnoreCase))
        {
            modFolder = parts.Length - 2;
            return true;
        }

        for (var i = 1; i < parts.Length - 1; i++)
        {
            var isScript = parts[i].Equals("Scripts", StringComparison.OrdinalIgnoreCase) && extension.Equals(".lua", StringComparison.OrdinalIgnoreCase);
            var isDll = parts[i].Equals("dlls", StringComparison.OrdinalIgnoreCase) && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
            if (!isScript && !isDll) continue;

            modFolder = i - 1;
            return true;
        }

        return false;
    }

    private static bool Found(GamePath root, string[] parts, int from, out GamePath target)
    {
        target = root / string.Join('/', parts.Skip(from));
        return true;
    }

    // Folders only: the last segment is the file, so a file called "Paks" is not a folder.
    private static int IndexOf(string[] parts, string folder, int before)
    {
        for (var i = 0; i < before; i++)
        {
            if (parts[i].Equals(folder, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static int IndexOfPair(string[] parts, string first, string second)
    {
        for (var i = 0; i < parts.Length - 2; i++)
        {
            if (parts[i].Equals(first, StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals(second, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
