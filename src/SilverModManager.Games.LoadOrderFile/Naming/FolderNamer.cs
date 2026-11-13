using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Naming;

/// <summary>Names a folder after itself when the format finds a file in it that makes it a mod.</summary>
public sealed class FolderNamer(ILoadOrderFileFormat format) : IExternalModNamer
{
    public IReadOnlyList<string> Names(AbsolutePath modFolder)
    {
        var name = modFolder.FileName;
        var isMod = modFolder.EnumerateFiles("*", recursive: true)
            .Any(file => format.ModOf(((RelativePath)name).Join(file.RelativeTo(modFolder))) is not null);
        return isMod ? [name] : [];
    }
}
