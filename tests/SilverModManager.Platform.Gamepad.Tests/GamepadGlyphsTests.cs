using FluentAssertions;

namespace SilverModManager.Platform.Gamepad.Tests;

public class GamepadGlyphsTests
{
    [Theory]
    [InlineData(GamepadType.Xbox, GamepadButton.A, "A")]
    [InlineData(GamepadType.Xbox, GamepadButton.Y, "Y")]
    [InlineData(GamepadType.Xbox, GamepadButton.LB, "LB")]
    [InlineData(GamepadType.Xbox, GamepadButton.Menu, "Menu")]
    [InlineData(GamepadType.SteamDeck, GamepadButton.B, "B")]
    [InlineData(GamepadType.Unknown, GamepadButton.X, "X")]
    [InlineData(GamepadType.PlayStation, GamepadButton.A, "Cross")]
    [InlineData(GamepadType.PlayStation, GamepadButton.B, "Circle")]
    [InlineData(GamepadType.PlayStation, GamepadButton.X, "Square")]
    [InlineData(GamepadType.PlayStation, GamepadButton.Y, "Triangle")]
    [InlineData(GamepadType.PlayStation, GamepadButton.RT, "R2")]
    [InlineData(GamepadType.PlayStation, GamepadButton.Menu, "Options")]
    public void TheLabelFollowsThePadsLayout(GamepadType type, GamepadButton button, string expected)
    {
        GamepadGlyphs.For(type, button).Should().Be(expected);
    }

    [Theory]
    [InlineData(GamepadButton.A, "B")]
    [InlineData(GamepadButton.B, "A")]
    [InlineData(GamepadButton.X, "Y")]
    [InlineData(GamepadButton.Y, "X")]
    public void NintendoSwapsTheFaceButtonsByPosition(GamepadButton button, string expected)
    {
        GamepadGlyphs.For(GamepadType.Nintendo, button).Should().Be(expected);
    }

    [Theory]
    [InlineData(GamepadButton.DPadUp)]
    [InlineData(GamepadButton.DPadLeft)]
    public void EveryDirectionIsTheDPad(GamepadButton button)
    {
        GamepadGlyphs.For(GamepadType.Xbox, button).Should().Be("D-pad");
    }

    [Fact]
    public void EveryButtonHasALabelOnEveryPad()
    {
        foreach (var type in Enum.GetValues<GamepadType>())
        foreach (var button in Enum.GetValues<GamepadButton>())
            GamepadGlyphs.For(type, button).Should().NotBeNullOrWhiteSpace($"{button} on {type}");
    }
}
