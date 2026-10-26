using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>A heal could not be planned or completed; the message says what to do about it.</summary>
public class HealException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The heal was not done";
}
