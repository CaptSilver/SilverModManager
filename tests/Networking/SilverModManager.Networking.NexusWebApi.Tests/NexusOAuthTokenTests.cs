using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.DTOs.OAuth;
using SilverModManager.Networking.NexusWebApi.Auth;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class NexusOAuthTokenTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void RoundTripsThroughJsonWithCamelCaseNames()
    {
        var token = new NexusOAuthToken("access", "refresh", ExpiresAt);

        var json = JsonSerializer.Serialize(token);

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("accessToken", "refreshToken", "expiresAt");
        document.RootElement.GetProperty("expiresAt").GetString().Should().Be("2030-01-02T03:04:05+00:00");
        JsonSerializer.Deserialize<NexusOAuthToken>(json).Should().Be(token);
    }

    [Fact]
    public void ParsesTheDocumentedShape()
    {
        const string json = """{"accessToken":"a","refreshToken":"r","expiresAt":"2030-01-02T03:04:05Z"}""";

        JsonSerializer.Deserialize<NexusOAuthToken>(json).Should().Be(new NexusOAuthToken("a", "r", ExpiresAt));
    }

    [Theory]
    [InlineData(-3600, true)]
    [InlineData(0, true)]
    [InlineData(4 * 60, true)]
    [InlineData(6 * 60, false)]
    [InlineData(3600, false)]
    public void HasExpiredAllowsAFiveMinuteMargin(int secondsFromNow, bool expired)
    {
        var token = new NexusOAuthToken("a", "r", DateTimeOffset.UtcNow.AddSeconds(secondsFromNow));

        token.HasExpired.Should().Be(expired);
    }

    [Fact]
    public void FromReplyAddsTheLifetimeToTheCreationTime()
    {
        var reply = new JwtTokenReply { AccessToken = "a", RefreshToken = "r", ExpiresIn = 3600, CreatedAt = 1_900_000_000 };

        var token = NexusOAuthToken.FromReply(reply);

        token.Should().Be(new NexusOAuthToken("a", "r", DateTimeOffset.FromUnixTimeSeconds(1_900_000_000 + 3600)));
    }

    [Theory]
    [InlineData(null, "r")]
    [InlineData("a", null)]
    [InlineData(null, null)]
    public void FromReplyRefusesAReplyWithoutBothTokens(string? access, string? refresh)
    {
        NexusOAuthToken.FromReply(new JwtTokenReply { AccessToken = access, RefreshToken = refresh, ExpiresIn = 3600 }).Should().BeNull();
    }

    [Fact]
    public void TryParseRejectsGarbage()
    {
        NexusOAuthToken.TryParse("not json", out _).Should().BeFalse();
        NexusOAuthToken.TryParse("""{"accessToken":"a"}""", out _).Should().BeFalse();
    }
}
