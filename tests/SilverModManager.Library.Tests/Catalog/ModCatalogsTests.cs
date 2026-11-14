using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Library.Tests.Catalog;

public class ModCatalogsTests
{
    private static IModCatalog Catalog(string name, bool serves)
    {
        var catalog = Substitute.For<IModCatalog>();
        catalog.DisplayName.Returns(name);
        catalog.Serves(Arg.Any<IGameData>()).Returns(serves);
        return catalog;
    }

    [Fact]
    public void ModCatalogsListsOnlyTheCatalogsThatServeTheGame()
    {
        var first = Catalog("First", true);
        var skipped = Catalog("Skipped", false);
        var last = Catalog("Last", true);
        var catalogs = new ModCatalogs([first, skipped, last]);

        catalogs.For(Substitute.For<IGameData>()).Should().Equal(first, last);
    }

    [Fact]
    public void ACatalogIsFoundByItsDisplayNameWhateverTheCase()
    {
        var thunderstore = Catalog("Thunderstore", true);
        var catalogs = new ModCatalogs([Catalog("Other", true), thunderstore]);

        catalogs.Named("thunderstore").Should().BeSameAs(thunderstore);
        catalogs.Named("nothing").Should().BeNull();
    }
}
