namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// The tools this app started, by pid, so the running-game detector can tell a tool left open from the game itself.
/// </summary>
public interface IStartedTools
{
    /// <summary>Records <paramref name="pid"/> as <paramref name="toolName"/> until the registration is disposed.</summary>
    IDisposable Register(int pid, string toolName);

    /// <summary>The name a pid was registered under, or null.</summary>
    string? NameOf(int pid);
}
