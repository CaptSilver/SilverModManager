using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Games.Versions;

/// <summary>
/// The version a game's executable carries. The reader in <c>NexusMods.Paths</c> answers from the managed assembly
/// attributes on Linux, so a native executable such as <c>SkyrimVR.exe</c> reads as 0.0.0.0; the numeric file version
/// is then read from the PE resource directly. 0.0.0.0 is what an unset version looks like, never a version to report.
/// </summary>
public static class GameExecutableVersion
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
            return PeFileVersion.ReadVersion(stream) is { } fixedInfo && IsSet(fixedInfo) ? fixedInfo : Optional<Version>.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return Optional<Version>.None;
        }
    }

    /// <summary>Whether <paramref name="version"/> is a version at all: a missing one and 0.0.0.0 are both "unset".</summary>
    internal static bool IsSet(Version? version) => version is not null && (version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0)) != (0, 0, 0, 0);
}
