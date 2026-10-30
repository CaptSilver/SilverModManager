using System.Text.Json;
using FluentAssertions;
using SilverModManager.Networking.Forums.Auth;

namespace SilverModManager.Networking.Forums.Tests;

public class OAuthTokenTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void FromReplyExpiresExpiresInSecondsAfterNow()
    {
        var reply = JsonSerializer.Deserialize<OAuthTokenReply>("""{"access_token":"a","refresh_token":"r","expires_in":3600}""");

        var token = OAuthToken.FromReply(reply!, Now);

        token.Should().Be(new OAuthToken("a", "r", Now.AddHours(1)));
    }

    [Fact]
    public void FromReplyIgnoresACreationTime()
    {
        var reply = JsonSerializer.Deserialize<OAuthTokenReply>("""{"access_token":"a","expires_in":60,"created_at":1000}""");

        var token = OAuthToken.FromReply(reply!, Now);

        token!.ExpiresAt.Should().Be(Now.AddSeconds(60));
    }

    [Fact]
    public void FromReplyAcceptsAReplyWithoutARefreshToken()
    {
        var token = OAuthToken.FromReply(new OAuthTokenReply { AccessToken = "a", ExpiresIn = 60 }, Now);

        token!.RefreshToken.Should().BeNull();
        token.CanRefresh.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromReplyRefusesAReplyWithoutAnAccessToken(string? access)
    {
        OAuthToken.FromReply(new OAuthTokenReply { AccessToken = access, RefreshToken = "r", ExpiresIn = 60 }, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3600, true)]
    public void HasExpiredHoldsFromFiveMinutesBeforeExpiry(int secondsPastMargin, bool expired)
    {
        var token = new OAuthToken("a", "r", Now);
        var now = Now - TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(secondsPastMargin);

        token.HasExpired(now).Should().Be(expired);
    }

    [Fact]
    public void CanRefreshNeedsARefreshToken()
    {
        new OAuthToken("a", "r", Now).CanRefresh.Should().BeTrue();
        new OAuthToken("a", null, Now).CanRefresh.Should().BeFalse();
    }

    [Theory]
    [InlineData("r")]
    [InlineData(null)]
    public void RoundTripsThroughJson(string? refresh)
    {
        var token = new OAuthToken("a", refresh, Now);

        OAuthToken.TryParse(token.ToJson(), out var parsed).Should().BeTrue();

        parsed.Should().Be(token);
    }

    [Fact]
    public void ToJsonUsesCamelCaseNames()
    {
        using var document = JsonDocument.Parse(new OAuthToken("a", "r", Now).ToJson());

        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("accessToken", "refreshToken", "expiresAt");
    }

    [Fact]
    public void TryParseReadsADocumentWithoutARefreshToken()
    {
        OAuthToken.TryParse("""{"accessToken":"a","expiresAt":"2030-01-02T03:04:05Z"}""", out var token).Should().BeTrue();

        token.Should().Be(new OAuthToken("a", null, Now));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"accessToken":"a"}""")]
    [InlineData("""{"expiresAt":"2030-01-02T03:04:05Z"}""")]
    [InlineData("null")]
    public void TryParseRejectsGarbageAndIncompleteDocuments(string json)
    {
        OAuthToken.TryParse(json, out _).Should().BeFalse();
    }

    [Fact]
    public void ToStringHidesTheTokens()
    {
        var text = new OAuthToken("secret-access", "secret-refresh", Now).ToString();

        text.Should().NotContain("secret-access").And.NotContain("secret-refresh");
    }
}
