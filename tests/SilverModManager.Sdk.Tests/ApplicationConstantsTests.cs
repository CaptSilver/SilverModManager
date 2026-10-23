using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class ApplicationConstantsTests
{
    [Fact]
    public void Test_BuildDate()
    {
        ApplicationConstants.BuildDate
            .Should().NotBe(DateTimeOffset.UnixEpoch, "it shouldn't be the default");
    }

    [Fact]
    public void Test_Identity()
    {
        ApplicationConstants.ProductName.Should().Be("SilverModManager");
        ApplicationConstants.ApplicationId.Should().Be("io.github.CaptSilver.SilverModManager");
        ApplicationConstants.DataDirectoryName.Should().Be("SilverModManager");
    }

    [Fact]
    public void Test_UserAgentIsProductName()
    {
        // SMM_USER_AGENT overrides the default, so only assert when it is unset.
        if (Environment.GetEnvironmentVariable(EnvironmentVariableNames.UserAgentName) is not null) return;
        ApplicationConstants.UserAgent.ApplicationName.Should().Be(ApplicationConstants.ProductName);
    }
}
