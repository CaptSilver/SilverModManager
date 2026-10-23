namespace SilverModManager.Sdk;

/// <summary>
/// Reads environment variables, so code that depends on them can be tested without touching the process.
/// </summary>
public interface IEnvironmentReader
{
    /// <summary>
    /// The value of the variable, or null when it is unset.
    /// </summary>
    string? Get(string name);
}

/// <summary>
/// Reads the real process environment.
/// </summary>
public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
