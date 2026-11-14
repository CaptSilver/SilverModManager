using FluentAssertions;
using Skyrim = SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests;

public class Skse64ShelfEntryTests
{
    [Fact]
    public void SkyrimSpecialEditionListsTheSkse64ReleaseOnItsShelf()
    {
        typeof(IGitHubShelfGame).IsAssignableFrom(typeof(Skyrim)).Should().BeTrue();

        var entry = Skyrim.Skse64Shelf;
        entry.Owner.Should().Be("ianpatt");
        entry.Repository.Should().Be("skse64");
        // The release asset is skse64_2_02_06.7z; the hint keeps a second archive in a release from being taken for it.
        entry.AssetSuffix.Should().Be(".7z");
        entry.NameHint.Should().Be("skse64_");
    }
}
