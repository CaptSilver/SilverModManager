using System.Runtime.InteropServices;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Where a game's order file is: in the game folder, or below one of the Windows known folders (inside the Proton
/// prefix for a Windows build). A native Linux build keeps the same file elsewhere, which <paramref name="NativeLinuxPath"/>
/// names below the XDG config folder.
/// </summary>
/// <remarks>There is no implicit conversion from a path: the root has to be visible at the row.</remarks>
public sealed record OrderFileLocation(ModLocationRoot Root, RelativePath Path, RelativePath? NativeLinuxPath = null)
{
    public static OrderFileLocation GameFolder(RelativePath path) => new(ModLocationRoot.GameFolder, path);

    /// <summary>The folder that holds the file, relative to <see cref="Root"/>.</summary>
    public RelativePath Folder => Path.Parent;

    /// <summary>
    /// The game path of the file. Outside the game folder the location already ends at the file's folder, so the path
    /// is the file name alone.
    /// </summary>
    public GamePath ToGamePath() => Root == ModLocationRoot.GameFolder
        ? new GamePath(LocationId.Game, Path)
        : new GamePath(SimpleLocations.For(Root), Path.FileName);

    /// <summary>The folder that holds the file, for the install <paramref name="result"/> describes; not meaningful for the game folder.</summary>
    public AbsolutePath ResolveFolder(IFileSystem fileSystem, GameLocatorResult result)
        => result.Platform == OSPlatform.Linux && NativeLinuxPath is { } native
            ? fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine(native.Parent)
            : SimpleLocations.Resolve(fileSystem, Root).Combine(Folder);
}
