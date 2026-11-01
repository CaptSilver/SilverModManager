using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// Builds a <c>ThunderstoreModSource</c> whose collaborators are substitutes unless a test supplies its own.
/// </summary>
internal static class ThunderstoreModSources
{
    public static ThunderstoreModSource Create(
        IConnection connection,
        IThunderstoreCatalogSource? catalogs = null,
        ThunderstoreUpdateChecker? checker = null,
        IThunderstoreInstaller? installer = null,
        IThunderstoreSource? source = null,
        params IGameData[] games)
    {
        return new ThunderstoreModSource(
            catalogs ?? Substitute.For<IThunderstoreCatalogSource>(),
            checker ?? new ThunderstoreUpdateChecker(connection),
            connection,
            installer ?? Substitute.For<IThunderstoreInstaller>(),
            source ?? Substitute.For<IThunderstoreSource>(),
            games);
    }
}
