using System.Runtime.InteropServices;

namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>The user and group the sandbox presents, so a test does not depend on who runs it.</summary>
public interface IUserIds
{
    uint Uid { get; }
    uint Gid { get; }
}

internal sealed class LibcUserIds : IUserIds
{
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();

    public uint Uid => GetUid();
    public uint Gid => GetGid();
}
