namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Registered only in the process that shows the window. A launch started from the command line outlives the verb
/// that began it, which is only true here: a headless main tears its database and launch session down when the verb
/// returns.
/// </summary>
public sealed class WindowHost;
