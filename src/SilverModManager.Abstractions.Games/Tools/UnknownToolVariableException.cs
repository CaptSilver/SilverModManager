namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// An argument names a <c>{Variable}</c> that has no value: it does not exist, or the game does not supply it.
/// </summary>
public sealed class UnknownToolVariableException(string variableName)
    : InvalidOperationException($"The tool's arguments use {{{variableName}}}, which has no value for this game.")
{
    public string VariableName { get; } = variableName;
}
