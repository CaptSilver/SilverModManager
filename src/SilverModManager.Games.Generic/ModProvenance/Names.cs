namespace SilverModManager.Games.Generic.ModProvenance;

internal static class Names
{
    /// <summary>A name with everything but letters and digits dropped, so "Adjust The FOV" and "AdjustTheFov" compare equal.</summary>
    public static string Squash(string? name) => name is null ? "" : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The part of a Thunderstore <c>Owner-Name</c> after the owner.</summary>
    public static string? ShortPackage(string? package)
        => package is { } p && p.IndexOf('-') is var dash and > 0 && dash < p.Length - 1 ? p[(dash + 1)..] : null;
}
