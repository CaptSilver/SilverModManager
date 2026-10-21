namespace NexusMods.Games.Declarative.Schema;

/// <summary>A definition file that isn't valid YAML for the schema (syntax error, unknown key, wrong value type).</summary>
public sealed class DefinitionException(string message, Exception? inner = null) : Exception(message, inner);
