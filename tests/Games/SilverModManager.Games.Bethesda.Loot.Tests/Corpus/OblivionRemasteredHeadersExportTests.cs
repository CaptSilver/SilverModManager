using System.Text;
using System.Text.Json;
using SilverModManager.Games.Bethesda.LoadOrder;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Corpus;

/// <summary>
/// Reads the plugin headers of a Mod Organizer instance of Oblivion Remastered, read-only, and writes
/// their names, flags and masters to a JSON file for tools/loot-oracle/generate_real.py. Not a check:
/// it only runs when all three variables are set (see tools/loot-oracle/README.md).
/// </summary>
public class OblivionRemasteredHeadersExportTests
{
    private const string InstanceVariable = "LOOT_ORACLE_OBLIVION_INSTANCE";
    private const string GameDataVariable = "LOOT_ORACLE_OBLIVION_GAME_DATA";
    private const string OutputVariable = "LOOT_ORACLE_HEADERS_OUT";

    [Fact]
    public void WriteOblivionRemasteredHeadersForOracle()
    {
        var instance = Environment.GetEnvironmentVariable(InstanceVariable);
        var gameData = Environment.GetEnvironmentVariable(GameDataVariable);
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(
            string.IsNullOrEmpty(instance) || string.IsNullOrEmpty(gameData) || string.IsNullOrEmpty(output),
            $"{InstanceVariable}, {GameDataVariable} and {OutputVariable} are not all set");

        const string profile = "Default";
        var profileDirectory = Path.Combine(instance!, "profiles", profile);
        var loadOrder = ReadNames(Path.Combine(profileDirectory, "loadorder.txt"));

        // Enabled mods are the "+" lines of modlist.txt. A plugin in a mod wins over the game's own copy.
        var enabledMods = System.IO.File.ReadLines(Path.Combine(profileDirectory, "modlist.txt"))
            .Where(line => line.StartsWith('+'))
            .Select(line => Path.Combine(instance!, "mods", line[1..], "Data"))
            .Where(System.IO.Directory.Exists)
            .ToList();
        var searchPath = enabledMods.Append(gameData!).ToList();

        var plugins = new List<object>();
        foreach (var name in loadOrder)
        {
            var path = searchPath.Select(directory => Path.Combine(directory, name)).FirstOrDefault(System.IO.File.Exists);
            path.Should().NotBeNull($"{name} is in the profile's load order and must be in an enabled mod or the game's Data folder");

            using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = PluginHeaderReader.Read(stream, PluginFormat.Tes4Oblivion, name);
            plugins.Add(new { name, flags = header.RecordFlags, masters = header.Masters });
        }

        var document = new { kind = "mod-organizer-instance", game = "Oblivion Remastered", profile, plugins };
        System.IO.File.WriteAllBytes(output!, new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n"));
    }

    private static List<string> ReadNames(string path) => System.IO.File.ReadLines(path)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .ToList();
}
