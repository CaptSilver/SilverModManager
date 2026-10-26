using System.Globalization;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.CLI.OptionParsers;

/// <summary>
/// Parses a string into a loadout marker
/// </summary>
[UsedImplicitly]
internal class LoadoutParser(IConnection conn, IOptionParser<IGame> gameParser) : IOptionParser<Loadout.ReadOnly>
{
    public bool TryParse(string input, out Loadout.ReadOnly value, out string error)
    {
        var db = conn.Db;
        error = string.Empty;
        if (EntityId.TryParseFromHex(input, out var parsedId))
        {
            var loadout = Loadout.Load(db, parsedId);
            if (loadout.IsValid())
            {
                value = loadout;
                return true;
            }
        }
        
        // An id in the format of: LoadoutId:00000000000000000000000000
        if (input.StartsWith("LoadoutId:") && input.Length == 25
            && ulong.TryParse(input[10..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var loadoutId))
        {
            var loadout = Loadout.Load(db, EntityId.From(loadoutId));
            if (loadout.IsValid())
            {
                value = loadout;
                return true;
            }
        }
        
        // A deleted loadout keeps its row until cleanup, and its name and short name are free for reuse, so the
        // lookups by name below skip it rather than let it shadow or duplicate a live loadout.

        // In the format of "<Game>/<ShortName>"
        if (input.Contains("/"))
        {
            var parts = input.Split('/');
            var game = parts[0];
            var shortName = parts[1];

            if (gameParser.TryParse(game, out var gameValue, out _))
            {
                if (Loadout
                    .FindByShortName(db, shortName)
                    .TryGetFirst(l => l.IsVisible() && l.Installation.Game == gameValue.GameId, out var foundLoadout))
                {
                    value = foundLoadout;
                    return true;
                }
            }
        }

        var found = Loadout.FindByName(db, input).Where(l => l.IsVisible()).ToArray();

        if (found.Length == 1)
        {
            value = found[0];
            return true;
        }

        value = default;
        error = found.Length == 0
            ? $"No loadout has the name or id '{input}'. Run `loadouts list` to see them."
            : $"More than one loadout is named '{input}'. Use its id from `loadouts list` instead.";
        return false;
    }
}
