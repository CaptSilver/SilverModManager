using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Ini;
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

            var found = ReadTestFiles(IniDocument.Parse(await file.ReadAllBytesAsync(cancellationToken)));
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
    internal static string?[] ReadTestFiles(string iniText) => ReadTestFiles(IniDocument.Parse(iniText));

    /// <inheritdoc cref="ReadTestFiles(string)"/>
    internal static string?[] ReadTestFiles(IniDocument ini) =>
        Enumerable.Range(1, KeyCount).Select(n => ini.Get("General", $"sTestFile{n}")).ToArray();
}
