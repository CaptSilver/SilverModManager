using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// An argument names a <c>{Variable}</c> that has no value: it does not exist, or the game does not supply it.
/// The arguments come from the tool's definition or the player's own tool, so the message is for the player.
/// </summary>
public sealed class UnknownToolVariableException(string variableName)
    : UserFacingException($"The tool's arguments use {MarkdownText.Code("{" + variableName + "}")}, which has no value for this game.")
{
    public string VariableName { get; } = variableName;

    /// <inheritdoc />
    public override string Title => "The tool was not run";
}
