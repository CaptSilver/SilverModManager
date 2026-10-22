namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A URL at which a plugin can be found. Follows libloot's metadata/location.rs.
/// </summary>
public sealed record Location(string Url)
{
    /// <summary>A name for the URL, such as the site name.</summary>
    public string? Name { get; init; }
}
