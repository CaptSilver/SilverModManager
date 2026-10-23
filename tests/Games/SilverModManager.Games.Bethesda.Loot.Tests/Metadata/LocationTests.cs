using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Tests.Metadata;

// libloot's metadata/location.rs tests all go through YAML; these cover the model behaviour they rely on.
public class LocationTests
{
    [Fact]
    public void ShouldOnlySetUrlByDefault()
    {
        var location = new Location("https://example.com");

        location.Url.Should().Be("https://example.com");
        location.Name.Should().BeNull();
    }

    [Fact]
    public void ShouldCompareByUrlAndName()
    {
        new Location("u") { Name = "n" }.Should().Be(new Location("u") { Name = "n" });
        new Location("u").Should().NotBe(new Location("u") { Name = "n" });
        new Location("u1").Should().NotBe(new Location("u2"));
    }
}
