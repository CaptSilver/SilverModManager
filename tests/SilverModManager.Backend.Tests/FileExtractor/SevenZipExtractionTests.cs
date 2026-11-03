using System.Formats.Tar;
using System.IO.Compression;
using FluentAssertions;
using SilverModManager.Backend.FileExtractor.Extractors;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class SevenZipExtractionTests : AFileExtractorTest
{
    [Fact]
    public async Task Test_Issue3003()
    {
        const string fileName = "zip-with-spaces.zip";
        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(fileName);
        archivePath.FileExists.Should().BeTrue();

        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(archivePath, destination, TestContext.Current.CancellationToken);

        // The archive stores "bar/foo /bar.txt"; the space after foo can't stay.
        var files = destination.Path.EnumerateFiles().ToArray();
        files.Select(f => f.RelativeTo(destination.Path).ToString()).Should().Equal("bar/foo/bar.txt");
        files.Should().OnlyContain(x => x.FileExists);
    }

    // 7z restores the Unix mode stored in a .7z or .rar entry, and an archive that stores 000 left collection
    // downloads unable to read their own extracted files. 7z-with-unix-modes.7z holds scripts/mod.reds at
    // 000, bin/run.sh at 755 and config/private.ini at 600 inside a 700 folder; the stored mode is ignored
    // apart from the execute bit.
    [Fact]
    public async Task ExtractedFilesGetDefaultModesWhateverTheArchiveStores()
    {
        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(UnixModesArchive, destination, TestContext.Current.CancellationToken);

        UnixFileMode Mode(string path) => File.GetUnixFileMode(destination.Path.Combine(path).ToString());
        var regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var executable = regular | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        Mode("scripts/mod.reds").Should().Be(regular);
        Mode("config/private.ini").Should().Be(regular);
        Mode("bin/run.sh").Should().Be(executable);
        Mode("config").Should().Be(executable);
        (await File.ReadAllTextAsync(destination.Path.Combine("scripts/mod.reds").ToString(), TestContext.Current.CancellationToken)).Should().Be("func Main() {}");
    }

    // An archive can carry symlinks, and chmod follows them: normalising must never change a link's target.
    [Fact]
    public async Task NormalizingModesLeavesSymlinkTargetsAlone()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var secretDir = outside.Path.Combine("secret");
        secretDir.CreateDirectory();
        var secretFile = secretDir.Combine("key");
        await secretFile.WriteAllTextAsync("private", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(secretFile.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(secretDir.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var extracted = TemporaryFileManager.CreateFolder();
        File.CreateSymbolicLink(extracted.Path.Combine("linked-file").ToString(), secretFile.ToString());
        Directory.CreateSymbolicLink(extracted.Path.Combine("linked-dir").ToString(), secretDir.ToString());

        SevenZipExtractor.NormalizeModes(extracted.Path.ToString());

        File.GetUnixFileMode(secretFile.ToString()).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(secretDir.ToString()).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // 7z-with-symlinks.7z holds link-abs (absolute), link-escape and link-dotdot (../ out of the root) and
    // sub/deep-escape (../../ out of sub), all aimed outside the extraction folder, plus link-file, link-chain
    // and link-sub-file (links to files inside it) and link-dir (a link to the folder sub). Nothing that
    // leaves the folder may survive, and links inside it become plain files or disappear.
    [Fact]
    public async Task ExtractedArchiveHasNoSymlinks()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var secret = outside.Path.Combine("outside-secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);

        var destination = outside.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(SymlinksArchive, destination, TestContext.Current.CancellationToken);

        var root = destination.ToString();
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray();
        entries.Where(e => new FileInfo(e).LinkTarget is not null).Should().BeEmpty();

        var relative = entries.Select(e => Path.GetRelativePath(root, e)).Order().ToArray();
        relative.Should().Equal("link-chain", "link-file", "link-sub-file", "real.txt", "sub", Path.Combine("sub", "inner.txt"));

        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        Read("link-file").Should().Be("real content");
        Read("link-chain").Should().Be("real content");
        Read("link-sub-file").Should().Be("inner content");
        Read("real.txt").Should().Be("real content");

        File.ReadAllText(secret.ToString()).Should().Be("private");
    }

    // 7z-with-unreadable-link-target.7z stores hidden.txt and the folder locked at mode 000 and link-hidden, a
    // link to hidden.txt. Modes have to be fixed before the links are swept, or the sweep can neither list
    // locked nor copy hidden.txt over the link.
    [Fact]
    public async Task LinkToAFileStoredAsUnreadableBecomesACopy()
    {
        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(UnreadableLinkTargetArchive, destination, TestContext.Current.CancellationToken);

        string Read(string path) => File.ReadAllText(destination.Path.Combine(path).ToString());
        new FileInfo(destination.Path.Combine("link-hidden").ToString()).LinkTarget.Should().BeNull();
        Read("link-hidden").Should().Be("hidden content");
        Read("hidden.txt").Should().Be("hidden content");
        Read("locked/inner.txt").Should().Be("locked content");
    }

    // A .tar.gz is two layers: 7z strips the gzip and leaves a .tar, which has to be opened as well or the
    // caller sees a single tar file instead of the archive's members.
    [Fact]
    public async Task ATarGzIsExtractedToItsMembersNotToTheInnerTar()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var archive = scratch.Path.Combine("release.tar.gz");
        WriteTarGz(archive, ("./bin/me3", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), ("bin/win64/me3_mod_host.dll", UnixFileMode.UserRead | UnixFileMode.UserWrite));

        (await FileExtractor.CanExtract(archive.Read())).Should().BeTrue("the gzip signature is recognised");

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        var relative = destination.EnumerateFiles().Select(f => f.RelativeTo(destination).ToString()).Order().ToArray();
        relative.Should().Equal("bin/me3", "bin/win64/me3_mod_host.dll");
        File.ReadAllText(destination.Combine("bin/me3").ToString()).Should().Be("bin/me3");
        File.GetUnixFileMode(destination.Combine("bin/me3").ToString()).HasFlag(UnixFileMode.UserExecute).Should().BeTrue("the stored execute bit survives");
    }

    // 7z names the inner tar after the archive, so a download saved without the .tar.gz suffix gives a tar with
    // some other name. It is still a tar and still has to be opened.
    [Fact]
    public async Task ATarGzSavedWithoutItsSuffixIsStillExtractedToItsMembers()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var archive = scratch.Path.Combine("release");
        WriteTarGz(archive, ("bin/me3", UnixFileMode.UserRead | UnixFileMode.UserWrite), ("bin/win64/me3_mod_host.dll", UnixFileMode.UserRead | UnixFileMode.UserWrite));

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        destination.EnumerateFiles().Select(f => f.RelativeTo(destination).ToString()).Order()
            .Should().Equal("bin/me3", "bin/win64/me3_mod_host.dll");
    }

    // The tar is opened from outside the destination, so a member that has the tar's own name is kept.
    [Fact]
    public async Task AMemberNamedLikeTheInnerTarIsKept()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var archive = scratch.Path.Combine("release.tar.gz");
        WriteTarGz(archive, ("release.tar", UnixFileMode.UserRead | UnixFileMode.UserWrite), ("bin/me3", UnixFileMode.UserRead | UnixFileMode.UserWrite));

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        destination.EnumerateFiles().Select(f => f.RelativeTo(destination).ToString()).Order()
            .Should().Equal("bin/me3", "release.tar");
        File.ReadAllText(destination.Combine("release.tar").ToString()).Should().Be("release.tar");
    }

    // A gzip of one ordinary file has no tar inside it, so the file itself is the result.
    [Fact]
    public async Task AGzipOfAPlainFileExtractsThatFile()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var archive = scratch.Path.Combine("notes.txt.gz");
        await using (var stream = archive.Create())
        await using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
            await gzip.WriteAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        File.ReadAllText(destination.Combine("notes.txt").ToString()).Should().Be("hello");
    }

    private static void WriteTarGz(AbsolutePath path, params (string Name, UnixFileMode Mode)[] entries)
    {
        using var stream = path.Create();
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
        foreach (var (name, mode) in entries)
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = mode,
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(name.TrimStart('.', '/'))),
            });
        }
    }

    private AbsolutePath UnreadableLinkTargetArchive => FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-unreadable-link-target.7z");

    private AbsolutePath SymlinksArchive =>FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-symlinks.7z");

    private AbsolutePath UnixModesArchive => FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-unix-modes.7z");

    // The 7zz that ships with the app has no pin anywhere else: the hash beside the binary is what a
    // reviewer compares with the one 7-zip.org publishes for the release.
    [Fact]
    public void BundledSevenZipMatchesItsPin()
    {
        var folder = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("runtimes/linux-x64/native");
        var binary = folder.Combine("7zz");
        var pin = folder.Combine("7zz.sha256");
        Assert.SkipUnless(binary.FileExists, "the system 7zz is used and nothing is bundled");

        pin.FileExists.Should().BeTrue("the bundled binary is pinned");
        var expected = File.ReadAllText(pin.ToString()).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary.ToString())));
        actual.Should().Be(expected.ToLowerInvariant());
    }

    [Fact]
    public async Task CanExtractToLongPath()
    {
        await using var tempFolder = TemporaryFileManager.CreateFolder();
        var dest = tempFolder.Path;

        // Create a long path
        while (!(dest.GetFullPathLength() > 280))
        {
            dest = dest.Combine("subfolder");
            FileSystem.CreateDirectory(dest);
        }

        dest.GetFullPathLength().Should().BeGreaterThan(280);

        FileSystem.CreateDirectory(dest);

        var file = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources/data_7zip_lzma2.7z");

        var act = async () => await FileExtractor.ExtractAllAsync(file, dest, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        var actual = await tempFolder.Path.EnumerateFiles()
            .ToAsyncEnumerable()
            .SelectAwait(async f => (f.RelativeTo(dest), await f.XxHash3Async()))
            .ToArrayAsync(TestContext.Current.CancellationToken);

        (RelativePath, Hash)[] expected = [
            ("deepFolder/deepFolder2/deepFolder3/deepFolder4/deepFile.txt", (Hash)0x3F0AB4D495E35A9A),
            ("folder1/folder1file.txt", (Hash)0x8520436F06348939),
            ("rootFile.txt", (Hash)0x818A82701BC1CC30),
        ];

        actual.Should().BeEquivalentTo(expected);
    }
}
