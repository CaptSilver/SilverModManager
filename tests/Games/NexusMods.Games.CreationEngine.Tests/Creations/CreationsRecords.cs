using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine.Tests.Creations;

/// <summary>Writes the files a Creation Engine game keeps its Creations in, into a game install under test.</summary>
internal static class CreationsRecords
{
    public static void WriteCatalog(AbsolutePath appData, params (string Key, string Title, string[] Files)[] entries)
    {
        var body = string.Join(",", entries.Select(e =>
            $"\"{e.Key}\": {{ \"AchievementSafe\": true, \"Files\": [{string.Join(",", e.Files.Select(f => $"\"{f}\""))}], \"Title\": \"{e.Title}\", \"Version\": \"1.0\" }}"));
        var json = "{ \"ContentCatalog\": { \"Description\": \"header\" }" + (body.Length > 0 ? "," + body : "") + " }";

        appData.CreateDirectory();
        File.WriteAllText((appData / "ContentCatalog.txt").ToString(), json);
    }

    public static void WriteManifest(AbsolutePath gameFolder, string fileName, params string[] entries)
    {
        var folder = gameFolder / "Creations";
        folder.CreateDirectory();
        File.WriteAllBytes((folder / fileName).ToString(), CreationsManifestReaderTests.Build(entries));
    }
}
