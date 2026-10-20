using NexusMods.Paths;
using NexusMods.Sdk.IO;

namespace NexusMods.Sdk.Tests.IO;

public class FileFingerprintTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).ToString(),
        $"file-fingerprint-tests-{Guid.NewGuid():N}");

    public FileFingerprintTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AbsolutePath At(string name) => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, name));

    [Fact]
    public void Of_ARegularFile_IsItsSizeAndModificationTime()
    {
        var file = At("plain");
        File.WriteAllText(file.ToString(), "12345");
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file.ToString(), modified);

        var fingerprint = FileFingerprint.Of(file);

        Assert.Equal(Size.From(5), fingerprint.Size);
        Assert.Equal(new DateTimeOffset(modified), fingerprint.LastModified);
    }

    [Fact]
    public void Of_ASymlink_IsTheTargetsSizeAndModificationTime()
    {
        var target = At("target");
        var link = At("link");
        File.WriteAllText(target.ToString(), "12345");
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target.ToString(), modified);
        File.CreateSymbolicLink(link.ToString(), target.ToString());

        var fingerprint = FileFingerprint.Of(link);

        Assert.Equal(Size.From(5), fingerprint.Size);
        Assert.Equal(new DateTimeOffset(modified), fingerprint.LastModified);
    }

    [Fact]
    public void Of_ASymlinkChain_IsTheFinalTargets()
    {
        var target = At("target");
        File.WriteAllText(target.ToString(), "12345");
        File.CreateSymbolicLink(At("middle").ToString(), target.ToString());
        File.CreateSymbolicLink(At("outer").ToString(), At("middle").ToString());

        Assert.Equal(Size.From(5), FileFingerprint.Of(At("outer")).Size);
    }

    [Fact]
    public void Of_ASymlinkToNothing_Throws()
    {
        File.CreateSymbolicLink(At("dangling").ToString(), At("missing").ToString());

        Assert.Throws<FileNotFoundException>(() => FileFingerprint.Of(At("dangling")));
    }
}
