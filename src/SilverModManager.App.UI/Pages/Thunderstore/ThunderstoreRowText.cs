using System.Globalization;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>
/// The text a Thunderstore row shows in place of the raw package data.
/// </summary>
public static class ThunderstoreRowText
{
    /// <summary>
    /// A package name as thunderstore.io and r2modman show it: underscores (which stand in for spaces in package
    /// names) become spaces, and an owner prefix is dropped because the row already says who the owner is. Empty
    /// when the name has nothing readable in it.
    /// </summary>
    public static string DisplayName(string name, string? owner)
    {
        var readable = Collapse(name);
        if (readable == "") return "";

        if (!string.IsNullOrEmpty(owner))
        {
            var ownerWords = Collapse(owner);
            // Only a whole leading word counts: "glarmers tools" is not "glarmer" plus "s tools".
            if (ownerWords != "" && readable.StartsWith(ownerWords, StringComparison.OrdinalIgnoreCase) && readable.Length > ownerWords.Length && readable[ownerWords.Length] is ' ' or '-')
            {
                var rest = readable[(ownerWords.Length + 1)..].Trim(' ', '-');
                if (rest != "") return rest;
            }
        }

        return readable;
    }

    /// <summary>
    /// What a package row on the library and loadout pages is called: the readable package name, or the user's own
    /// name when they renamed the library item. A name that is still the one the download gave (the archive file
    /// name, or the older "Namespace-Name") counts as not renamed.
    /// </summary>
    public static string RowName(string @namespace, string name, string versionNumber, string libraryItemName)
    {
        var downloaded = $"{@namespace}-{name}";
        var isDefault = string.IsNullOrWhiteSpace(libraryItemName)
            || libraryItemName.Equals($"{downloaded}-{versionNumber}.zip", StringComparison.OrdinalIgnoreCase)
            || libraryItemName.Equals(downloaded, StringComparison.OrdinalIgnoreCase);
        if (!isDefault) return libraryItemName;

        var readable = DisplayName(name, @namespace);
        return readable == "" ? downloaded : readable;
    }

    /// <summary>1,234,567 as "1.2M"; one decimal below ten of a unit, none above.</summary>
    public static string CompactCount(long count)
    {
        if (count <= 0) return "0";
        if (count < 1_000) return count.ToString(CultureInfo.InvariantCulture);

        var (divisor, suffix) = count switch
        {
            < 1_000_000 => (1_000d, "K"),
            < 1_000_000_000 => (1_000_000d, "M"),
            _ => (1_000_000_000d, "B"),
        };

        // Truncate rather than round, so 999,999 reads 999K and never "1000K".
        var scaled = Math.Floor(count / divisor * 10) / 10;
        return (scaled >= 10 ? Math.Floor(scaled).ToString(CultureInfo.InvariantCulture) : scaled.ToString("0.#", CultureInfo.InvariantCulture)) + suffix;
    }

    private static string Collapse(string text) => string.Join(' ', text.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries));
}
