using System.Text.RegularExpressions;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Static class for providing new Loadout names.
/// </summary>
public static partial class LoadoutNameProvider
{
    /// <summary>
    /// Returns a capital one or two letter short name for a loadout.
    /// It will cycle through the alphabet, starting with 'A' and continue to AA.
    /// If all two letter combinations are used, a duplicate name will be returned.
    /// Short names should not be assumed to be unique, they are only used for user disambiguation.
    /// </summary>
    public static string GetNewShortName(ReadOnlySpan<string> existingLoadoutsShortNames)
    {
        for (var i = 'A'; i <= 'Z'; i++)
        {
            var found = false;
            foreach (var name in existingLoadoutsShortNames)
            {
                if (name.Length != 1) continue;
                if (name[0] != i) continue;

                found = true;
                break;
            }

            if (!found) return $"{i}";
        }

        for (var i = 'A'; i <= 'Z'; i++)
        {
            for (var j = 'A'; j <= 'Z'; j++)
            {
                var found = false;
                foreach (var name in existingLoadoutsShortNames)
                {
                    if (name.Length != 2) continue;
                    if (name[0] != i || name[1] != j) continue;

                    found = true;
                    break;
                }

                if (!found) return $"{i}{j}";
            }
        }

        return "ZZ";
    }

    /// <summary>
    /// The name a new loadout gets when the user does not pick one: the game's name, or "&lt;game&gt; 2",
    /// "&lt;game&gt; 3", ... (the first free number) when that is taken. Taken names are compared ignoring case
    /// and outer whitespace.
    /// </summary>
    public static string GetDefaultName(string gameName, IEnumerable<string> existingLoadoutNames)
    {
        var baseName = gameName.Trim();
        var taken = existingLoadoutNames.Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = baseName;
        for (var i = 2; taken.Contains(result); i++)
        {
            result = $"{baseName} {i}";
        }

        return result;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is one an older build gave a loadout automatically: "Loadout " and a one or
    /// two letter short name, with the " (2)" style suffix a clash with another loadout added.
    /// </summary>
    public static bool IsLegacyDefaultName(string name) => LegacyDefaultName().IsMatch(name);

    [GeneratedRegex(@"^Loadout [A-Z]{1,2}( \([0-9]+\))?$")]
    private static partial Regex LegacyDefaultName();
}
