using FluentAssertions;

namespace NexusMods.Sdk.Tests;

public class ApplicationConstantsTests
{
    [Fact]
    public void Test_BuildDate()
    {
        ApplicationConstants.BuildDate
            .Should().NotBe(DateTimeOffset.UnixEpoch, "it shouldn't be the default");
    }
}
