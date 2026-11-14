namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Which label set a pad's buttons carry, so a hint can say "A" on an Xbox pad and "Cross" on a PlayStation one.
/// </summary>
public enum GamepadType
{
    Unknown,
    Xbox,
    PlayStation,
    Nintendo,
    SteamDeck,
}

public enum StickSide
{
    Left,
    Right,
}

/// <summary>A pad that is plugged in right now.</summary>
public sealed record ConnectedPad(string Name, GamepadType Type);
