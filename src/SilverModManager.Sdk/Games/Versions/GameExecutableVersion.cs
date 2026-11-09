using System.Text.RegularExpressions;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Games.Versions;

/// <summary>
/// The version a game's executable carries. The reader in <c>NexusMods.Paths</c> answers from the managed assembly
/// attributes on Linux, so a native executable such as <c>SkyrimVR.exe</c> reads as 0.0.0.0; the PE resource is then
/// read directly. Its string table comes before the numeric block: <c>SkyrimVR.exe</c> ships 1.0.0.0 as the numeric
/// file version, a placeholder, and the real 1.4.15.0 only in the string table. An executable that has no readable
/// string version falls back to the numeric one. 0.0.0.0 is what an unset version looks like, never a version to report.
/// </summary>
public static partial class GameExecutableVersion
{
    public static Optional<Version> Read(AbsolutePath executable)
    {
        try
        {
            if (!executable.FileExists) return Optional<Version>.None;

            var info = executable.FileInfo.GetFileVersionInfo();
            if (IsSet(info.ProductVersion)) return info.ProductVersion;
            if (IsSet(info.FileVersion)) return info.FileVersion;

            using var stream = executable.Read();
            if (ParseProductVersion(PeFileVersion.ReadProductVersion(stream)) is { } written && IsSet(written)) return written;

            // ReadVersion scans from wherever the stream is, and the string table read has moved it.
            stream.Position = 0;
            return PeFileVersion.ReadVersion(stream) is { } fixedInfo && IsSet(fixedInfo) ? fixedInfo : Optional<Version>.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return Optional<Version>.None;
        }
    }

    /// <summary>
    /// The four numbers a version resource's <c>ProductVersion</c> text starts with, written <c>1.4.15.0</c> or
    /// <c>1, 4, 15, 0</c>; anything after the fourth number is ignored. Null when the text does not start that way.
    /// </summary>
    internal static Version? ParseProductVersion(string? text)
    {
        if (text is null) return null;

        var match = ProductVersionPattern().Match(text);
        if (!match.Success) return null;

        var parts = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(match.Groups[i + 1].ValueSpan, out parts[i])) return null;
        }

        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    [GeneratedRegex(@"^\s*(\d+)\s*[.,]\s*(\d+)\s*[.,]\s*(\d+)\s*[.,]\s*(\d+)")]
    private static partial Regex ProductVersionPattern();

    /// <summary>Whether <paramref name="version"/> is a version at all: a missing one and 0.0.0.0 are both "unset".</summary>
    internal static bool IsSet(Version? version) => version is not null && (version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0)) != (0, 0, 0, 0);
}
