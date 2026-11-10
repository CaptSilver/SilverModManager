using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// What makes two crash logs the same crash. <see cref="EngineFrames"/> is the Address Library ids of the first three
/// probable frames in the game executable (a frame without one is <c>exe+offset</c>); never the stripped symbol,
/// whose identity lives in the suffix the display drops, and never a foreign DLL's offset or an absolute address,
/// which move between runs. The fault module stays in the key because the ids are the nearest preceding ones, not
/// per-function: the same id can sit under the executable in one crash and under <c>d3d11.dll</c> in another.
/// </summary>
public readonly record struct CrashSignature(string FaultModule, string EngineFrames, GamePath? SubjectFile)
{
    private const int EngineFrameCount = 3;

    public static CrashSignature Of(CrashReport report)
    {
        var frames = report.Frames
            .Where(f => f.Kind == 'P' && f.IsExecutable)
            .Take(EngineFrameCount)
            .Select(f => f.AddressLibraryId ?? $"{f.Module}+{f.Offset}");

        // Filtering on the extension keeps a relevant plugin (a TESNPC's .esp) out of the key.
        var subject = report.RelevantObjects
            .Select(o => o.Parenthetical is null ? null : CrashLogPaths.Classify(o.Parenthetical))
            .FirstOrDefault(p => p is { } path && CrashLogPaths.IsMesh(path));

        return new CrashSignature(report.FaultModule ?? "", string.Join(',', frames), subject);
    }
}
