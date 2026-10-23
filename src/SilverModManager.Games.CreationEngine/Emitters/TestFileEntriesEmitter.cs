using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Emitters;

/// <summary>
/// Warns when the game's ini sets <c>sTestFile1</c> to <c>sTestFile10</c>. Fallout 4, Fallout 4 VR and Starfield then
/// ignore Plugins.txt and the Creation Club list (libloadorder game_settings.rs); the app does not model that, so
/// this only makes the gap visible.
/// </summary>
/// <remarks>
/// Starfield also layers a <c>Starfield_{language}.INI</c> between the two files read here; that one is not read.
/// </remarks>
public class TestFileEntriesEmitter(ICreationEngineGame game) : ILoadoutDiagnosticEmitter
{
    private const int KeyCount = 10;

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();

        // Index 0 is sTestFile1. A later file overrides an earlier one key by key, even with an empty value.
        var values = new string?[KeyCount];
        foreach (var iniFile in game.TestFileIniFiles)
        {
            var locations = loadout.InstallationInstance.Locations;
            if (!locations.ContainsKey(iniFile.LocationId)) continue;

            var file = locations.ToAbsolutePath(iniFile);
            if (!file.FileExists) continue;

            var text = await file.ReadAllTextAsync(cancellationToken);
            var found = ReadTestFiles(text);
            for (var i = 0; i < KeyCount; i++)
                if (found[i] is not null) values[i] = found[i];
        }

        var keys = Enumerable.Range(0, KeyCount)
            .Where(i => !string.IsNullOrEmpty(values[i]))
            .Select(i => $"sTestFile{i + 1}")
            .ToList();
        if (keys.Count > 0)
            yield return Diagnostics.CreateTestFileEntries(string.Join(", ", keys));
    }

    /// <summary>The <c>sTestFileN</c> values of the <c>[General]</c> section; keys that are not there stay null.</summary>
    internal static string?[] ReadTestFiles(string iniText)
    {
        var values = new string?[KeyCount];
        var inGeneral = false;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[')
            {
                inGeneral = line.TrimEnd().Equals("[General]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inGeneral) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key = line[..eq].Trim();
            if (!key.StartsWith("sTestFile", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(key["sTestFile".Length..], out var number) || number is < 1 or > KeyCount) continue;

            values[number - 1] = line[(eq + 1)..].Trim();
        }
        return values;
    }
}
