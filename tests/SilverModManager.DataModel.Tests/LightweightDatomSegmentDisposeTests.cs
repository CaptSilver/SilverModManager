using FluentAssertions;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.Query.SliceDescriptors;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The segment owns a native iterator. It has to release it exactly once, whether the caller walks the segment to
/// the end, fast-forwards past the end, or abandons it half way: a missed release leaks the native handle, a second
/// one frees it twice.
/// </summary>
public class LightweightDatomSegmentDisposeTests
{
    private sealed class DisposeCounter
    {
        public int Count;
    }

    private struct FakeEnumerator(DisposeCounter counter, ulong[] entities) : IRefDatomEnumerator
    {
        private int _index = -1;

        public bool MoveNext<TSliceDescriptor>(TSliceDescriptor descriptor, bool useHistory = false)
            where TSliceDescriptor : ISliceDescriptor, allows ref struct
        {
            _index++;
            return _index < entities.Length;
        }

        public KeyPrefix KeyPrefix => new(0, 0) { E = EntityId.From(entities[_index]) };
        public Ptr Current => default;
        public Ptr ValueSpan => default;
        public Ptr ExtraValueSpan => default;

        public void Dispose() => counter.Count++;
    }

    private static LightweightDatomSegment<FakeEnumerator, EntityIdSlice> Create(DisposeCounter counter, params ulong[] entities) =>
        new(new FakeEnumerator(counter, entities), SliceDescriptor.Create(EntityId.From(0)));

    [Fact]
    public void DisposingAnUntouchedSegmentReleasesTheIterator()
    {
        var counter = new DisposeCounter();
        Create(counter, 1, 2).Dispose();
        counter.Count.Should().Be(1);
    }

    [Fact]
    public void DisposingAHalfWalkedSegmentReleasesTheIterator()
    {
        var counter = new DisposeCounter();
        var segment = Create(counter, 1, 2, 3);
        segment.MoveNext().Should().BeTrue();
        segment.Dispose();
        counter.Count.Should().Be(1);
    }

    [Fact]
    public void DisposingAfterFastForwardingPastTheEndReleasesTheIteratorOnce()
    {
        var counter = new DisposeCounter();
        var segment = Create(counter, 1, 2, 3);
        segment.FastForwardTo(EntityId.From(10)).Should().BeFalse();
        segment.Dispose();
        counter.Count.Should().Be(1);
    }

    [Fact]
    public void DisposingAnEmptySegmentAfterFastForwardReleasesTheIteratorOnce()
    {
        var counter = new DisposeCounter();
        var segment = Create(counter);
        segment.FastForwardTo(EntityId.From(1)).Should().BeFalse();
        segment.Dispose();
        counter.Count.Should().Be(1);
    }

    [Fact]
    public void DisposingTwiceReleasesTheIteratorOnce()
    {
        var counter = new DisposeCounter();
        var segment = Create(counter, 1);
        segment.Dispose();
        segment.Dispose();
        counter.Count.Should().Be(1);
    }

    [Fact]
    public void ADisposedSegmentDoesNotAdvanceTheReleasedIterator()
    {
        var counter = new DisposeCounter();
        var segment = Create(counter, 1, 2);
        segment.Dispose();
        segment.MoveNext().Should().BeFalse();
        segment.FastForwardTo(EntityId.From(2)).Should().BeFalse();
    }
}
