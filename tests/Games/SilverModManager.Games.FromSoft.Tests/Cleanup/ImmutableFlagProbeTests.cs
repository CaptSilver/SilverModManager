using FluentAssertions;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Cleanup;

/// <summary>
/// The flag itself cannot be set without root, so these only check that the ioctl import answers for ordinary and
/// missing files.
/// </summary>
public class ImmutableFlagProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-immutable-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IImmutableFlagProbe Probe() => new IoctlImmutableFlagProbe();

    [Fact]
    public void AnOrdinaryFileIsNotImmutable()
    {
        var path = Path.Combine(_root, "start_protected_game.exe");
        File.WriteAllText(path, "x");

        Probe().IsImmutable(FileSystem.Shared.FromUnsanitizedFullPath(path)).Should().BeFalse();
    }

    [Fact]
    public void AMissingFileIsNotImmutable()
    {
        Probe().IsImmutable(FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "missing.exe"))).Should().BeFalse();
    }
}
