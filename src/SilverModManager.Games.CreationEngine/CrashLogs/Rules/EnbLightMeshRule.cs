using SilverModManager.Abstractions.Games;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

/// <summary>
/// An object in the call stack carries ENB Light: a node named <c>ENB Light ...</c> or a texture under
/// <c>textures/effects/enb/</c>, in its own block or any parent's. ENB Light does not work in Skyrim VR, so there the
/// mod that supplies the object's own mesh is the fix. A bare <c>NiBillboardNode</c> is not evidence: vanilla flames
/// and glow effects use one too.
/// </summary>
public sealed class EnbLightMeshRule : ICrashCauseRule
{
    private const string EnbTextureFolder = "Data/textures/effects/enb/";
    private const string Drawn = "A mesh with ENB particle lights was drawn; ";

    public CrashVerdict? Match(CrashReport report, CrashOwnership owners, ICreationEngineGame game)
    {
        var evidence = report.ObjectDetails.FirstOrDefault(HasEnbLight);
        if (evidence is null) return null;

        var mesh = MeshOf(evidence);
        if (game is not IVirtualRealityGame)
        {
            return new CrashVerdict(
                Drawn + "this is a lead: ENB Light works on Skyrim SE, so look at the mesh's other changes",
                "No known fix", null, false, mesh);
        }

        return new CrashVerdict(
            Drawn + "ENB Light does not work in Skyrim VR",
            "Disable the mod that supplies the mesh, or replace the mesh with a version without ENB Light",
            mesh is { } m ? owners.OwnerOf(m)?.Reference : null, false, mesh);
    }

    private static bool HasEnbLight(ObjectDetails details) => details.All.Any(IsEnbLightLine);

    private static bool IsEnbLightLine(DetailLine line)
    {
        if (line.Key == "Name") return line.Value.StartsWith("ENB Light", StringComparison.Ordinal);
        if (!line.Key.StartsWith("Texture", StringComparison.Ordinal) && !line.Key.StartsWith("EffectTexture", StringComparison.Ordinal)) return false;
        return CrashLogPaths.Classify(line.Value) is { } texture && texture.Path.ToString().StartsWith(EnbTextureFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The object's own mesh: its parenthetical when that is one, else the first mesh named by a <c>File</c> line of its own block.</summary>
    private static GamePath? MeshOf(ObjectDetails details)
    {
        if (details.Parenthetical is { } text && CrashLogPaths.Classify(text) is { } named && CrashLogPaths.IsMesh(named)) return named;
        return details.Own
            .Where(l => l.Key == "File")
            .Select(l => CrashLogPaths.Classify(l.Value))
            .FirstOrDefault(p => p is { } path && CrashLogPaths.IsMesh(path));
    }
}
