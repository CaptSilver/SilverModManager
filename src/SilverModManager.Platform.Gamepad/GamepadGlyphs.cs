namespace SilverModManager.Platform.Gamepad;

/// <summary>What is printed on a button of a given kind of pad, for the hint bar.</summary>
public static class GamepadGlyphs
{
    public static string For(GamepadType type, GamepadButton button) => button switch
    {
        GamepadButton.DPadUp or GamepadButton.DPadDown or GamepadButton.DPadLeft or GamepadButton.DPadRight => "D-pad",
        GamepadButton.LeftStick => "L stick",
        GamepadButton.RightStick => "R stick",
        _ => type switch
        {
            GamepadType.PlayStation => PlayStation(button),
            GamepadType.Nintendo => Nintendo(button),
            _ => Xbox(button),
        },
    };

    // Steam Deck and unknown pads use Xbox's layout and letters.
    private static string Xbox(GamepadButton button) => button switch
    {
        GamepadButton.A => "A",
        GamepadButton.B => "B",
        GamepadButton.X => "X",
        GamepadButton.Y => "Y",
        GamepadButton.LB => "LB",
        GamepadButton.RB => "RB",
        GamepadButton.LT => "LT",
        GamepadButton.RT => "RT",
        GamepadButton.View => "View",
        GamepadButton.Menu => "Menu",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };

    private static string PlayStation(GamepadButton button) => button switch
    {
        GamepadButton.A => "Cross",
        GamepadButton.B => "Circle",
        GamepadButton.X => "Square",
        GamepadButton.Y => "Triangle",
        GamepadButton.LB => "L1",
        GamepadButton.RB => "R1",
        GamepadButton.LT => "L2",
        GamepadButton.RT => "R2",
        GamepadButton.View => "Create",
        GamepadButton.Menu => "Options",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };

    // A Nintendo pad prints A on the right-hand button where Xbox prints B, and the same swap for X and Y.
    private static string Nintendo(GamepadButton button) => button switch
    {
        GamepadButton.A => "B",
        GamepadButton.B => "A",
        GamepadButton.X => "Y",
        GamepadButton.Y => "X",
        GamepadButton.LB => "L",
        GamepadButton.RB => "R",
        GamepadButton.LT => "ZL",
        GamepadButton.RT => "ZR",
        GamepadButton.View => "-",
        GamepadButton.Menu => "+",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };
}
