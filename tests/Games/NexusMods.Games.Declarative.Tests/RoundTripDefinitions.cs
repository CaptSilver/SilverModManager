using System.Text;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Paths;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>
/// The definitions the round-trip tests cover: the ones embedded in the game assembly plus a fixture
/// that guarantees the theory is never empty. Read straight from the sources so the test data can be
/// built before the service container exists.
/// </summary>
internal static class RoundTripDefinitions
{
    public static DefinitionSource Fixture
    {
        get
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Resources", "games", "roundtrip-fixture.yaml");
            return new DefinitionSource("test/roundtrip-fixture.yaml", File.ReadAllText(file));
        }
    }

    public static IReadOnlyList<GameDefinition> All()
    {
        var assembly = typeof(GameDefinitionCatalog).Assembly;
        var definitions = new List<GameDefinition>();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("games/", StringComparison.Ordinal) && name.EndsWith(".yaml", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            definitions.Add(DefinitionSerializer.Read(reader.ReadToEnd()));
        }

        definitions.Add(DefinitionSerializer.Read(Fixture.Text));
        return definitions;
    }

    /// <summary>
    /// The files a located install has before any mod: the executable and one more. Neither is in the
    /// stubbed build the hash service vouches for, so the loadout takes them in as the player's own and
    /// they have to come through every apply.
    /// </summary>
    public static Dictionary<RelativePath, byte[]> VanillaFiles(GameDefinition definition) => new()
    {
        [RelativePath.FromUnsanitizedInput(definition.PrimaryExe)] = Encoding.UTF8.GetBytes("executable"),
        [RelativePath.FromUnsanitizedInput("vanilla/data.pak")] = Encoding.UTF8.GetBytes("vanilla data"),
    };

    public static string FolderName(string definitionId, string plan) => $"{definitionId}-{plan}";

    public static readonly string[] Plans = ["default", "copy"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var definition in All())
        {
            foreach (var plan in Plans) yield return [definition.Id, plan];
        }
    }
}
