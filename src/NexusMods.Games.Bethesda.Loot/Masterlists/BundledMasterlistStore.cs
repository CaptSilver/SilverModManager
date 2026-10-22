using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NexusMods.Games.Bethesda.Loot.Masterlists;

/// <summary>
/// Serves the masterlists embedded in this assembly, which tools/loot-masterlists/update.sh
/// downloads. The lock file next to them records when each one was retrieved.
/// </summary>
public sealed class BundledMasterlistStore : IMasterlistStore
{
    private static readonly Assembly Assembly = typeof(BundledMasterlistStore).Assembly;
    private readonly IReadOnlyDictionary<string, LockEntry> _lock = ReadLock();

    private readonly record struct LockEntry(DateTimeOffset Retrieved, string Commit);

    public MasterlistContent Current(LootGame game)
    {
        var repository = game.MasterlistRepository();
        return new MasterlistContent(MasterlistText(repository), PreludeText(), BundledRevision(repository));
    }

    /// <param name="repository">A name from <see cref="LootGameExtensions.BundledRepositories"/>.</param>
    public string MasterlistText(string repository) => ReadResource($"loot/{repository}/masterlist.yaml");

    public string PreludeText() => ReadResource("loot/prelude.yaml");

    /// <summary>
    /// When the bundled copy of <paramref name="repository"/> was downloaded, as a UTC date.
    /// </summary>
    public DateTimeOffset RetrievedOn(string repository) => Entry(repository).Retrieved;

    /// <summary>
    /// Names the bundled copy of <paramref name="repository"/> by the commit the lock records for it.
    /// </summary>
    public string BundledRevision(string repository)
    {
        var entry = Entry(repository);
        return $"{repository} {entry.Commit[..Math.Min(entry.Commit.Length, 10)]} (bundled, retrieved {entry.Retrieved:yyyy-MM-dd})";
    }

    private LockEntry Entry(string repository) =>
        _lock.TryGetValue(repository, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No lock entry for LOOT repository \"{repository}\"");

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Embedded LOOT resource \"{name}\" is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, LockEntry> ReadLock()
    {
        using var document = JsonDocument.Parse(ReadResource("loot/masterlists.lock.json"));
        var result = new Dictionary<string, LockEntry>();
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var date = DateTime.ParseExact(LockString(file, "retrieved"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            result[LockString(file, "repo")] = new LockEntry(new DateTimeOffset(date), LockString(file, "commit"));
        }
        return result;
    }

    private static string LockString(JsonElement file, string property) =>
        file.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"masterlists.lock.json has a null \"{property}\" entry");
}
