namespace SilverModManager.Games.Generic.Tests.ModProvenance;

/// <summary>A throwaway folder tree standing in for a home directory, a game folder and a manager's data.</summary>
internal sealed class Tree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("silvermod-provenance-").FullName;

    public string Home => Path.Combine(Root, "home");

    public string GameFolder => Path.Combine(Root, "Game");

    public void Dispose() => Directory.Delete(Root, recursive: true);

    public string Path_(string relative) => Path.Combine(Root, relative);

    public string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A folder path as Wine's Z: drive shows it, the way a ModOrganizer.ini stores it.</summary>
    public static string WinPath(string unixPath) => "Z:" + unixPath.Replace('/', '\\');

    /// <summary>A ModOrganizer.ini whose game is <paramref name="gamePath"/>, written the way Qt escapes it.</summary>
    public static string Mo2Ini(string gameName, string gamePath, string extraSettings = "")
        => $"[General]\ngameName={gameName}\ngamePath=@ByteArray({WinPath(gamePath).Replace("\\", "\\\\")})\n\n[Settings]\n{extraSettings}\n";

    public static string MetaIni(string? modId, string? fileId, string version = "1.0", string? archive = null, string? repository = null)
        => $"[General]\nmodid={modId ?? "0"}\nversion={version}\ninstallationFile={archive}\n{(repository is null ? "" : $"repository={repository}\n")}\n[installedFiles]\n"
           + (fileId is null ? "size=0\n" : $"1\\modid={modId}\n1\\fileid={fileId}\nsize=1\n");
}
