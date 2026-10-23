using System.IO.Compression;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Tests.Fixtures;

/// <summary>
/// Builds a <c>.wabbajack</c> file (a zip holding a <c>modlist</c> entry and GUID-named blobs) in a test folder.
/// </summary>
public static class WabbajackFixture
{
    /// <summary>The checked-in real modlist, see Fixtures/NOTICE.</summary>
    public static string RealModlistPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "MO2AndSKSETest.wabbajack");

    public static AbsolutePath Write(
        AbsolutePath folder,
        ModList list,
        IReadOnlyDictionary<string, byte[]>? blobs = null,
        string fileName = "list.wabbajack")
    {
        folder.CreateDirectory();
        var path = folder.Combine(fileName);
        using var file = File.Create(path.ToString());
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        AddEntry(zip, "modlist", ModListJsonWriter.Write(list));
        foreach (var (id, bytes) in blobs ?? new Dictionary<string, byte[]>())
            AddEntry(zip, id, bytes);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(bytes);
    }

    public static ModList EmptyList(string name = "Test list", params Directive[] directives) => new(
        Name: name, Author: "", Description: "", Version: "1.0.0.0", WabbajackVersion: null,
        GameType: "SkyrimSpecialEdition", OtherGames: [], Image: "", Readme: "", Website: "", Community: "",
        IsNsfw: false, Archives: [], Directives: directives);
}
