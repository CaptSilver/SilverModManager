using System.Text.Json;
using FluentAssertions;
using NexusMods.Networking.GOG;

namespace NexusMods.Networking.GOG.Tests;

public class GogCredentialTests
{
    private static readonly GogCredential Credential = new("access", "refresh", "session", 1234567890123UL, new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));

    [Fact]
    public void RoundTripsThroughJson()
    {
        GogCredential.TryParse(Credential.ToJson(), out var parsed).Should().BeTrue();
        parsed.Should().Be(Credential);
    }

    [Fact]
    public void WritesCamelCaseNames()
    {
        using var document = JsonDocument.Parse(Credential.ToJson());
        var root = document.RootElement;
        root.GetProperty("accessToken").GetString().Should().Be("access");
        root.GetProperty("refreshToken").GetString().Should().Be("refresh");
        root.GetProperty("sessionId").GetString().Should().Be("session");
        root.GetProperty("userId").GetUInt64().Should().Be(1234567890123UL);
        root.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(Credential.ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"accessToken\":\"a\"}")]
    public void RejectsWhatIsNotACredential(string json)
    {
        GogCredential.TryParse(json, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void ToStringDoesNotShowTheTokens()
    {
        Credential.ToString().Should().NotContain("access").And.NotContain("refresh");
    }
}
