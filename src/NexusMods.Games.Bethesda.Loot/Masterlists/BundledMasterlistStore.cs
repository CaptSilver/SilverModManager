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
    private readonly IReadOnlyDictionary<string, DateTimeOffset> _retrieved = ReadLock();

    public MasterlistContent Current(LootGame game) => new(MasterlistText(game.MasterlistRepository()), PreludeText());

    /// <param name="repository">A name from <see cref="LootGameExtensions.BundledRepositories"/>.</param>
    public string MasterlistText(string repository) => ReadResource($"loot/{repository}/masterlist.yaml");

    public string PreludeText() => ReadResource("loot/prelude.yaml");

    /// <summary>
    /// When the bundled copy of <paramref name="repository"/> was downloaded, as a UTC date.
    /// </summary>
    public DateTimeOffset RetrievedOn(string repository) =>
        _retrieved.TryGetValue(repository, out var date)
            ? date
            : throw new KeyNotFoundException($"No lock entry for LOOT repository \"{repository}\"");

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Embedded LOOT resource \"{name}\" is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, DateTimeOffset> ReadLock()
    {
        using var document = JsonDocument.Parse(ReadResource("loot/masterlists.lock.json"));
        var result = new Dictionary<string, DateTimeOffset>();
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var date = DateTime.ParseExact(LockString(file, "retrieved"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            result[LockString(file, "repo")] = new DateTimeOffset(date);
        }
        return result;
    }

    private static string LockString(JsonElement file, string property) =>
        file.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"masterlists.lock.json has a null \"{property}\" entry");
}
