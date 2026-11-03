namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>A file a condition needs could not be read, so the condition can be neither true nor false.</summary>
public sealed class ConditionEvaluationException(string fileName, Exception inner)
    : Exception($"Could not read \"{fileName}\" while evaluating a condition: {inner.Message}", inner)
{
    /// <summary>The path as the condition wrote it.</summary>
    public string FileName { get; } = fileName;
}
