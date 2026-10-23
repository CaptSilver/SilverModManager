using System.Security.Cryptography;
using NexusMods.Paths;

namespace SilverModManager.SingleProcess;

/// <summary>
/// The secret a client has to present to have the main process run a game for it. The CLI server listens on a
/// loopback port that any local user can reach, and a launch starts an arbitrary command; the token lives in a file
/// only the owner can read, so only a process of the same user can ask.
/// </summary>
internal static class LaunchToken
{
    /// <summary>
    /// Writes a fresh token over any earlier one, readable by the owner only from the first byte, and returns it.
    /// </summary>
    public static string Create(AbsolutePath path)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var file = path.GetFullPath();

        // A file left by an earlier run keeps its old mode when it is overwritten.
        File.Delete(file);
        using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new StreamWriter(stream);
        writer.Write(token);
        return token;
    }

    /// <summary>
    /// The token the main process left, or null when there is none to read.
    /// </summary>
    public static string? Read(AbsolutePath path)
    {
        try
        {
            var text = File.ReadAllText(path.GetFullPath()).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Matches(string expected, string? given)
        => given is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(given));
}
