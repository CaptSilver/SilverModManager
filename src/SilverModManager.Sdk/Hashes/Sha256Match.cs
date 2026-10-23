namespace NexusMods.Sdk.Hashes;

/// <summary>
/// Digest comparison that fails closed: a digest that is missing, empty or malformed never matches.
/// </summary>
public static class Sha256Match
{
    /// <summary>
    /// True only when both sides are present, <paramref name="expectedHex"/> is a valid 64 character
    /// hex digest, and the bytes are equal.
    /// </summary>
    public static bool Matches(Sha256Value? actual, string? expectedHex)
    {
        if (actual is null || string.IsNullOrEmpty(expectedHex)) return false;
        return Sha256Value.TryParse(expectedHex, out var expected) && expected.Equals(actual.Value);
    }
}
