using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Naming;

/// <summary>
/// Names a folder after each descriptor file in it, the file's name without its extension: XCOM 2 lists a mod by the
/// name of its <c>.XComMod</c> file, and one workshop item can hold several.
/// </summary>
public sealed class FileStemNamer(Extension descriptor) : IExternalModNamer
{
    public IReadOnlyList<string> Names(AbsolutePath modFolder) => modFolder.EnumerateFiles("*", recursive: false)
        .Where(file => file.FileName.EndsWith(descriptor.ToString(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => file.FileName, StringComparer.Ordinal)
        .Select(file => Path.GetFileNameWithoutExtension(file.FileName))
        .ToList();
}
