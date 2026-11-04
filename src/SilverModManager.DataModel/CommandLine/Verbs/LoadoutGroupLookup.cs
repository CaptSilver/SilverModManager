using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>A verb refused its input; the message says what to change and the exit code is 1.</summary>
public sealed class LoadoutVerbRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The command was not run";
}

/// <summary>
/// Finds the group a <c>-g</c> option names. A name is matched first; when no group has it, the text is tried as the
/// id <c>loadout groups list --json</c> prints.
/// </summary>
internal static class LoadoutGroupLookup
{
    /// <exception cref="LoadoutVerbRefusedException">No group matches, or the name matches several.</exception>
    public static LoadoutItemGroup.ReadOnly Find(Loadout.ReadOnly loadout, string nameOrId)
    {
        var groups = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup().ToArray();

        var byName = groups.Where(g => g.AsLoadoutItem().Name == nameOrId).ToArray();
        if (byName.Length == 1) return byName[0];
        if (byName.Length > 1)
        {
            var ids = string.Join(", ", byName.Select(g => g.Id.ToString()));
            throw new LoadoutVerbRefusedException($"{byName.Length} groups are called {MarkdownText.Code(nameOrId)} ({MarkdownText.Code(ids)}). Pass one of those ids to -g instead of the name.");
        }

        if (EntityId.TryParseFromHex(nameOrId, out var id))
        {
            var byId = groups.Where(g => g.Id == id).ToArray();
            if (byId.Length == 1) return byId[0];
        }

        throw new LoadoutVerbRefusedException($"Group {MarkdownText.Code(nameOrId)} was not found in loadout {MarkdownText.Code(loadout.Name)}. 'loadout groups list' shows the groups and their ids.");
    }
}
