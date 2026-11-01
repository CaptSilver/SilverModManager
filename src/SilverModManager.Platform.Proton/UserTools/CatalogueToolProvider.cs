using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>
/// Lists the catalogue tools the user has installed (rows with a tool id) whose definition applies to the
/// loadout's game. <see cref="UserToolProvider"/> lists the rest.
/// </summary>
public sealed class CatalogueToolProvider(IConnection connection, ModdingToolFactory factory, IEnumerable<ToolDefinition> definitions) : IToolProvider
{
    private readonly Dictionary<string, ToolDefinition> _byId = definitions
        .Where(d => d.Id.HasValue)
        .GroupBy(d => d.Id.GetValueOrDefault().Value)
        .ToDictionary(g => g.Key, g => g.First());

    /// <inheritdoc/>
    public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout)
    {
        if (loadout.InstallationInstance.Game is not IGame game) yield break;

        foreach (var row in UserTool.FindByInstallation(connection.Db, loadout.InstallationId))
        {
            if (row.ToolId.ValueOrDefault() is not { } id || _byId.TryGetValue(id, out var definition) is false) continue;
            if (definition.AppliesTo(game) is false) continue;

            var exe = FileSystem.Shared.FromUnsanitizedFullPath(row.Exe);
            yield return factory.Create(definition, row.ToInstalledTool(exe), game.GameId);
        }
    }
}
