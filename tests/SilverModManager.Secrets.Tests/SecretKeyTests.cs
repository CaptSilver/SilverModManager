using FluentAssertions;
using Xunit;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Secrets.Tests;

public class SecretKeyTests
{
    [Theory]
    [InlineData("nexus.api-key")]
    [InlineData("gog.oauth")]
    [InlineData("fromsoft.ersc-password")]
    [InlineData("github")]
    public void Valid_names_are_accepted(string name)
    {
        new SecretKey(name).Name.Should().Be(name);
    }

    [Theory]
    [InlineData("Nexus.api-key")]
    [InlineData("nexus api-key")]
    [InlineData(".nexus")]
    [InlineData("")]
    [InlineData("nexus.")]
    [InlineData("nexus..key")]
    [InlineData("-nexus")]
    [InlineData("nexus_key")]
    public void Invalid_names_are_rejected(string name)
    {
        var act = () => new SecretKey(name);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Default_key_is_not_a_valid_key()
    {
        var act = () => default(SecretKey).EnvironmentVariable;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Names_of_64_characters_are_accepted_and_65_are_rejected()
    {
        new SecretKey(new string('a', 64)).Name.Should().HaveLength(64);

        var act = () => new SecretKey(new string('a', 65));
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("nexus.api-key", "SILVERMM_SECRET_NEXUS_API_KEY")]
    [InlineData("fromsoft.ersc-password", "SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD")]
    [InlineData("gog.oauth", "SILVERMM_SECRET_GOG_OAUTH")]
    public void Environment_variable_is_derived_from_the_name(string name, string expected)
    {
        new SecretKey(name).EnvironmentVariable.Should().Be(expected);
    }

    [Fact]
    public void ToString_is_the_name()
    {
        new SecretKey("nexus.api-key").ToString().Should().Be("nexus.api-key");
    }
}
