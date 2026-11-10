using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>A crash log on disk: where it is, when the game wrote it (host time) and how big it is.</summary>
public sealed record CrashLogFile(AbsolutePath Path, DateTime LastWriteUtc, Size Size);
