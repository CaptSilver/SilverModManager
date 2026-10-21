namespace NexusMods.Games.Declarative.Tests;

internal static class Samples
{
    /// <summary>A definition that uses every key of the schema.</summary>
    public const string Full = """
        id: oblivion
        name: "The Elder Scrolls IV: Oblivion"
        nexus: { domain: oblivion, id: 101 }
        stores:
          steam: [22330]
          gog: [1458058109]
          epic: ["abc123"]
          amazon: ["amzn1.adg.product.oblivion"]
          manualOnly: false
        primaryExe: Oblivion.exe
        nativeLinux: false
        locations:
          Game: { root: install }
          AppData: { root: "%LOCALAPPDATA%", path: Oblivion }
          Preferences: { root: "%MYGAMES%", path: Oblivion }
          Saves: { root: "%MYGAMES%", path: Oblivion/Saves }
        aliases: ["Oblivion", "TES4"]
        installer:
          dataMarkers: [meshes, textures, sound]
          target: Game/Data
          fallback: Game
        deployment: { usesVfs: false }
        runtime: { proton: optional }
        capabilities:
          creationEngine:
            dataFolder: Data
            pluginsFile: { location: AppData, path: Plugins.txt, format: plain }
            parser: none
            scriptExtender: { loader: obse_loader.exe, files: ['obse_loader\.exe', 'obse_1_2_416\.dll'] }
            stopPatterns: ['(^|/)obse(/|$)']
            officialContent: []
        tools: ["Tools/Construction Set.exe"]
        """;

    /// <summary>The smallest definition the validator accepts, with the given id, steam app id and primary exe.</summary>
    public static string Minimal(string id, uint steam, string exe = "Game.exe", string extra = "") => $$"""
        id: {{id}}
        name: {{id}} name
        stores: { steam: [{{steam}}] }
        primaryExe: {{exe}}
        locations:
          Game: { root: install }
        {{extra}}
        """;
}
