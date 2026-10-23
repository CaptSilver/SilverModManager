using FluentAssertions;
using NexusMods.App.UI.Controls.TopBar;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;

namespace NexusMods.UI.Tests.Controls.SecretStoreNotice;

public class AccountTooltipTests
{
    private static INexusCredentialSummary Summary(NexusCredentialKind kind, string source)
    {
        var summary = Substitute.For<INexusCredentialSummary>();
        summary.Kind.Returns(kind);
        summary.Source.Returns(source);
        return summary;
    }

    [Theory]
    [InlineData("environment variable NEXUS_API_KEY", "via environment variable")]
    [InlineData("--secrets-file /run/secrets/nexus.json", "via --secrets-file")]
    public void AnOverride_NamesWhereTheCredentialCameFrom(string source, string expected)
    {
        AccountTooltipText.Describe("bob", Summary(NexusCredentialKind.ApiKey, source))
            .Should().Be($"Logged in to Nexus Mods as bob {expected}");
    }

    [Theory]
    [InlineData("Secret Service")]
    [InlineData("private file")]
    public void AStoredCredential_AddsNothing(string source)
    {
        AccountTooltipText.Describe("bob", Summary(NexusCredentialKind.OAuthToken, source))
            .Should().Be("Logged in to Nexus Mods as bob");
    }

    [Fact]
    public void NoCredential_AddsNothing()
    {
        AccountTooltipText.Describe("bob", Summary(NexusCredentialKind.None, ""))
            .Should().Be("Logged in to Nexus Mods as bob");
    }
}
