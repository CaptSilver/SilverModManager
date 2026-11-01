using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Fills in a tool's argument template from a loadout. The values are host paths: the tool runner translates the
/// arguments of a Windows exe for Wine, so doing it here as well would translate twice.
/// </summary>
public sealed class ToolArgumentResolver
{
    /// <summary>
    /// The template's words with the loadout's variables, the game's, and <paramref name="extra"/> filled in.
    /// </summary>
    /// <param name="extra">Values only the caller knows, such as the run's output folder or the tool's own path.</param>
    /// <exception cref="UnknownToolVariableException">A placeholder has no value.</exception>
    public IReadOnlyList<string> Resolve(ArgumentTemplate template, Loadout.ReadOnly loadout, IReadOnlyDictionary<string, string>? extra = null) =>
        template.Substitute(Variables(loadout, extra));

    /// <summary>
    /// For a line the user typed. It is split into words first, because substituting first would break a value
    /// that holds spaces (<c>-D:{DataFolder}</c> with a game folder called <c>Skyrim Special Edition</c>) into
    /// several words.
    /// </summary>
    public IReadOnlyList<string> ResolveLine(string line, Loadout.ReadOnly loadout, IReadOnlyDictionary<string, string>? extra = null) =>
        Resolve(new ArgumentTemplate(ArgumentSplitter.Split(line)), loadout, extra);

    private static Dictionary<string, string> Variables(Loadout.ReadOnly loadout, IReadOnlyDictionary<string, string>? extra)
    {
        var installation = loadout.InstallationInstance;
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ToolVariableNames.GameFolder] = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty)).GetFullPath(),
            [ToolVariableNames.LoadoutName] = loadout.Name ?? string.Empty,
        };

        if (installation.Game is IToolVariableSource source)
        {
            foreach (var (name, path) in source.ToolVariables(loadout))
                variables[name] = installation.Locations.ToAbsolutePath(path).GetFullPath();
        }

        if (extra is not null)
        {
            foreach (var (name, value) in extra)
                variables[name] = value;
        }

        return variables;
    }
}
