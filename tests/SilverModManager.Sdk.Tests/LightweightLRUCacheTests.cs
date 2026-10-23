using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class LightweightLRUCacheTests
{
    [Fact]
    public void AddedItemsCanBeRetrieved()
    {
        var cache = new LightweightLRUCache<int, int>(2);

        cache.Add(1, 1);
        cache.Count.Should().Be(1);

        cache.Add(2, 2);
        cache.Count.Should().Be(2);

        cache.Add(3, 3);
        cache.Count.Should().Be(2, "Cache has a size of 2");

        cache.TryGet(1, out _).Should().BeFalse("Item with key 3 replaced item with key 1 because of the max size of 2");

        cache.TryGet(2, out var value).Should().BeTrue();
        value.Should().Be(2);

        cache.TryGet(3, out value).Should().BeTrue();
        value.Should().Be(3);
    }

    [Fact]
    public void DuplicateItemsAreUpdated()
    {
        var cache = new LightweightLRUCache<int, int>(2);

        cache.Add(1, 1);
        cache.Count.Should().Be(1);

        cache.Add(1, 2);
        cache.Count.Should().Be(1, "Item with key 1 got updated, no new item was added");

        cache.TryGet(1, out var value).Should().BeTrue();
        value.Should().Be(2, "Item with key 1 got updated from value 1 to value 2");
    }
}
