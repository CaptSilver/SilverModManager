using System.Globalization;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Where a tool that writes to a folder it is given writes during one run. The prefix's <c>drive_c</c> is the one
/// place every Wine strategy can see and write: Steam's Proton script sees the whole host, but the umu and Heroic
/// paths may start a container that shares only the home folder and what the runner adds. The folder is a plain
/// host folder for the capture step, and <see cref="WindowsPathTranslator"/> turns it into a <c>C:\</c> path.
/// </summary>
public static class ToolRunFolder
{
    /// <summary>The folder under <c>drive_c</c> that holds every tool's run folders.</summary>
    public static readonly RelativePath Root = RelativePath.FromUnsanitizedInput("drive_c/SilverModManager/tool-output");

    /// <summary>
    /// Creates <c>{prefix}/drive_c/SilverModManager/tool-output/{tool}/{yyyyMMdd-HHmmss}</c>. A folder a run
    /// left behind in the same second is never reused: its files would be captured as this run's.
    /// </summary>
    public static AbsolutePath Create(AbsolutePath prefixRoot, ToolId tool, DateTimeOffset now)
    {
        var parent = prefixRoot / Root / RelativePath.FromUnsanitizedInput(tool.Value);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = parent / stamp;
        for (var attempt = 2; folder.DirectoryExists(); attempt++)
            folder = parent / $"{stamp}-{attempt}";

        folder.CreateDirectory();
        return folder;
    }
}
