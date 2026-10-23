using System.Text;
using System.Text.Json;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.Bethesda.Loot.Tests.Corpus;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Masterlists;

/// <summary>
/// Writes the condition-free masterlists the oracle generator sorts with. Not a check: it only runs
/// when LOOT_ORACLE_STRIP_DIR names the directory to write into (see tools/loot-oracle/README.md).
/// </summary>
public class StrippedMasterlistExportTests
{
    private const string DirectoryVariable = "LOOT_ORACLE_STRIP_DIR";

    [Fact]
    public void WriteStrippedMasterlistsForOracle()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(directory), $"{DirectoryVariable} is not set");
        Directory.CreateDirectory(directory!);

        var hashes = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var repo in LootGameExtensions.BundledRepositories)
        {
            var name = $"{repo}.masterlist.yaml";
            var file = StrippedMasterlists.Masterlist(repo);
            WriteUtf8(Path.Combine(directory, name), file.Stripped);
            hashes[name] = new { bundled = file.BundledSha256, stripped = file.StrippedSha256 };
        }

        var prelude = StrippedMasterlists.Prelude();
        WriteUtf8(Path.Combine(directory, "prelude.yaml"), prelude.Stripped);
        hashes["prelude.yaml"] = new { bundled = prelude.BundledSha256, stripped = prelude.StrippedSha256 };

        WriteUtf8(Path.Combine(directory, "sha256.json"),
            JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    // No BOM and no newline translation, so the file's bytes are the ones that were hashed.
    private static void WriteUtf8(string path, string text) =>
        System.IO.File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
}
