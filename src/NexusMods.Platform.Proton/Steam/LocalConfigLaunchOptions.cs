using NexusMods.Paths;

namespace NexusMods.Platform.Proton.Steam;

/// <summary>
/// Reads an app's launch options out of <c>localconfig.vdf</c> without touching the file.
/// </summary>
public static class LocalConfigLaunchOptions
{
    /// <summary>
    /// What <see cref="Read"/> found.
    /// </summary>
    /// <param name="FileReadable">False when the file is missing or cannot be parsed.</param>
    /// <param name="Line">The launch options, or null when the app has none.</param>
    public sealed record Result(bool FileReadable, string? Line);

    /// <summary>
    /// The launch options Steam has stored for <paramref name="appId"/>. A missing or malformed file is a
    /// result, not an error: both mean Steam has nothing configured that we can see.
    /// </summary>
    public static Result Read(AbsolutePath localConfig, uint appId)
    {
        var root = VdfFile.Read(localConfig);
        if (root is null) return new Result(false, null);

        var app = root["Software"]["Valve"]["Steam"]["apps"][appId.ToString()];
        var line = app["LaunchOptions"].Value;
        return new Result(true, string.IsNullOrEmpty(line) ? null : line);
    }
}
