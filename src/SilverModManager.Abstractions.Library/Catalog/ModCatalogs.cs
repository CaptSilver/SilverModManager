using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// The registered <see cref="IModCatalog"/>s, beside <see cref="ModSourceResolver"/>.
/// </summary>
public sealed class ModCatalogs(IEnumerable<IModCatalog> catalogs)
{
    private readonly IModCatalog[] _catalogs = catalogs.ToArray();

    /// <summary>
    /// The catalogs with mods for the game, in registration order.
    /// </summary>
    public IReadOnlyList<IModCatalog> For(IGameData game) => _catalogs.Where(catalog => catalog.Serves(game)).ToArray();

    /// <summary>
    /// The catalog with this display name, or null.
    /// </summary>
    public IModCatalog? Named(string name)
        => _catalogs.FirstOrDefault(catalog => catalog.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
}
