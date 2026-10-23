using Xunit;
using FluentAssertions;
using NexusMods.Secrets.File;

namespace NexusMods.Secrets.Tests.File;

public class UnixFileOwnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("unix-file-owner-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string CreateFile()
    {
        var path = Path.Combine(_directory, "file");
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_file_we_created_is_owned_by_the_effective_uid()
    {
        UnixFileOwner.IsOwnedByCurrentUser(CreateFile()).Should().BeTrue();
    }

    [Fact]
    public void The_owner_is_the_effective_uid()
    {
        UnixFileOwner.GetOwner(CreateFile()).Should().Be(UnixFileOwner.EffectiveUid());
    }

    [Fact]
    public void A_different_uid_is_not_the_owner()
    {
        var path = CreateFile();
        UnixFileOwner.IsOwnedBy(path, UnixFileOwner.EffectiveUid() + 1).Should().BeFalse();
        UnixFileOwner.IsOwnedBy(path, UnixFileOwner.GetOwner(path)).Should().BeTrue();
    }

    [Fact]
    public void A_dangling_symlink_still_reports_its_own_owner()
    {
        // Following the link would fail with ENOENT; only a no-follow lookup can answer for a dangling one.
        var link = Path.Combine(_directory, "link");
        System.IO.File.CreateSymbolicLink(link, Path.Combine(_directory, "missing-target"));

        UnixFileOwner.GetOwner(link).Should().Be(UnixFileOwner.EffectiveUid());
    }

    [Fact]
    public void A_missing_file_throws_rather_than_reporting_an_owner()
    {
        var act = () => UnixFileOwner.GetOwner(Path.Combine(_directory, "missing"));
        act.Should().Throw<IOException>();
    }
}
