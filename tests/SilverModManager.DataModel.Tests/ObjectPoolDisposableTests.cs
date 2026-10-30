using FluentAssertions;
using Microsoft.Extensions.ObjectPool;
using SilverModManager.DataModel.Extensions;

namespace SilverModManager.DataModel.Tests;

public class ObjectPoolDisposableTests
{
    private static ObjectPool<object> CreatePool() => new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<object>());

    [Fact]
    public void Value_IsTheRentedObjectUntilDisposed()
    {
        var pool = CreatePool();
        var rented = pool.RentDisposable();

        rented.Value.Should().NotBeNull();
    }

    [Fact]
    public void Value_ThrowsObjectDisposedAfterDispose()
    {
        var rented = CreatePool().RentDisposable();
        rented.Dispose();

        var read = () => rented.Value;

        read.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Value_OnADefaultInstanceThrowsObjectDisposedInsteadOfReturningNull()
    {
        var neverRented = default(ObjectPoolDisposable<object>);

        var read = () => neverRented.Value;

        read.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_OnADefaultInstanceDoesNothing()
    {
        var neverRented = default(ObjectPoolDisposable<object>);

        var dispose = () => neverRented.Dispose();

        dispose.Should().NotThrow();
    }
}
