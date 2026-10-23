using System.Text;
using FluentAssertions;
using SilverModManager.Networking.Steam.DTOs;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class AuthDataTests
{
    [Fact]
    public void LoadRoundTripsSavedData()
    {
        var saved = new AuthData { Username = "user", RefreshToken = "refresh" }.Save();

        var loaded = AuthData.Load(saved);

        loaded.Username.Should().Be("user");
        loaded.RefreshToken.Should().Be("refresh");
    }

    [Fact]
    public void LoadingJsonNullIsInvalidData()
    {
        var act = () => AuthData.Load(Encoding.UTF8.GetBytes("null"));

        act.Should().Throw<InvalidDataException>();
    }
}
