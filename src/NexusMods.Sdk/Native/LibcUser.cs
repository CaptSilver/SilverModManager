using System.Runtime.InteropServices;

namespace NexusMods.Sdk.Native;

/// <summary>Who this process runs as, which .NET does not expose.</summary>
public static partial class LibcUser
{
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint getegid();

    /// <summary>The effective user id, the owner of every file this process creates.</summary>
    public static uint EffectiveUserId => geteuid();

    /// <summary>The effective group id.</summary>
    public static uint EffectiveGroupId => getegid();
}
