using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Tests.Games;

public class StopRequestTests
{
    [Fact]
    public void Set_IsIdempotentAndObservable()
    {
        var request = new StopRequest();
        request.Requested.Should().BeFalse();

        request.Set();
        request.Set();

        request.Requested.Should().BeTrue();
    }

    [Fact]
    public async Task Set_IsSeenByAnotherThread()
    {
        var request = new StopRequest();
        await Task.Run(request.Set);
        request.Requested.Should().BeTrue();
    }
}
