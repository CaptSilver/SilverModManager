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

    // .NET lists a name that is not valid UTF-8 with replacement characters, so the path it hands back names nothing.
    // Normalising must still reach those entries, and still leave a link with such a name alone.
    [Fact]
    public async Task NormalizingModesReachesEntriesWhoseNamesAreNotUtf8()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var secret = outside.Path.Combine("secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(secret.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var secretDir = outside.Path.Combine("secret-dir");
        secretDir.CreateDirectory();
        File.SetUnixFileMode(secretDir.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var extracted = TemporaryFileManager.CreateFolder();
        var root = extracted.Path.ToString();
        byte[] file = [(byte)'f', 0xFF, (byte)'a'];
        byte[] script = [(byte)'s', 0xFE];
        byte[] folder = [(byte)'d', 0xFD];
        byte[] fileLink = [(byte)'l', 0xFC];
        byte[] folderLink = [(byte)'m', 0xFB];
        RawSymlink.CreateFile(root, file, 0);
        RawSymlink.CreateFile(root, script, UnixFileMode.UserExecute);
        RawSymlink.CreateFolder(root, folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RawSymlink.CreateFile(root, [.. folder, (byte)'/', .. "inner.txt"u8], 0);
        RawSymlink.Create(root, fileLink, secret.ToString());
        RawSymlink.Create(root, folderLink, secretDir.ToString());

        try
        {
            SevenZipExtractor.NormalizeModes(root);

            var regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            var executable = regular | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            RawSymlink.ModeOf(root, file).Should().Be(regular);
            RawSymlink.ModeOf(root, script).Should().Be(executable);
            RawSymlink.ModeOf(root, folder).Should().Be(executable);
            RawSymlink.ModeOf(root, [.. folder, (byte)'/', .. "inner.txt"u8]).Should().Be(regular, "a folder whose name is not UTF-8 is entered");
            File.GetUnixFileMode(secret.ToString()).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(secretDir.ToString()).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            // A name that is not UTF-8 cannot be deleted by the temporary folder's cleanup.
            RawSymlink.Remove(root, [.. folder, (byte)'/', .. "inner.txt"u8]);
            foreach (var name in new[] { file, script, fileLink, folderLink, folder }) RawSymlink.Remove(root, name);
        }
    }

    // 7z 26.05 prints no "Folder" line for a .7z entry; the D in its attributes is the only sign of a folder.
    [Theory]
    [InlineData("Attributes = D", true)]
    [InlineData("Attributes = D drwxr-xr-x", true)]
    [InlineData("Attributes = A -rwxr-xr-x", false)]
    [InlineData("Attributes = A", false)]
    [InlineData("Folder = +\nAttributes = A", true)]
    public void ParsingAListingTakesAFolderFromItsAttributes(string fields, bool isFolder)
    {
        var listing = System.Text.Encoding.UTF8.GetBytes($"Path = f\n{fields}\n");

        var entries = SevenZipExtractor.ParseListing(listing);

        entries.Should().ContainSingle().Which.IsDirectory.Should().Be(isFolder);
    }

    // The scratch folder of a repeated name can hold a link, and listing it must not walk into the folder it names.
    [Fact]
    public async Task ListingScratchContentsDoesNotFollowALinkToAFolderOutside()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        await outside.Path.Combine("secret.txt").WriteAllTextAsync("private", TestContext.Current.CancellationToken);
        await using var scratch = TemporaryFileManager.CreateFolder();
        scratch.Path.Combine("d").CreateDirectory();
        await scratch.Path.Combine("d/b.txt").WriteAllTextAsync("b", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(scratch.Path.Combine("folder-link").ToString(), outside.Path.ToString());
        File.CreateSymbolicLink(scratch.Path.Combine("file-link").ToString(), outside.Path.Combine("secret.txt").ToString());

        var (files, links) = SevenZipExtractor.ListExtracted(scratch.Path.ToString());

        files.Select(f => Path.GetRelativePath(scratch.Path.ToString(), f)).Should().BeEquivalentTo("d/b.txt");
        links.Select(f => Path.GetFileName(f)).Should().BeEquivalentTo("folder-link", "file-link");
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

    // What 7z 26.05 prints for a link it refuses; extraction carries on without the link. The old build
    // printed "Dangerous symbolic link path", which a system 7z can still print.
    [Theory]
    [InlineData("ERROR: Dangerous link path was ignored : l3 : l1/../../outside")]
    [InlineData("ERROR: Dangerous link via another link was ignored : l3 : l1/x")]
    [InlineData("ERROR: Dangerous symbolic link path was ignored : l3 : ../x")]
    public void ARefusedLinkIsNotAnExtractionFailure(string line)
    {
        SevenZipExtractor.AreAllDangerousLinkErrors([line, "ERROR: Dangerous link path was ignored : l1 : ."]).Should().BeTrue();
    }

    [Fact]
    public void AnythingBesideRefusedLinksIsAnExtractionFailure()
    {
        SevenZipExtractor.AreAllDangerousLinkErrors([]).Should().BeFalse();
        SevenZipExtractor.AreAllDangerousLinkErrors([
            "ERROR: Dangerous link via another link was ignored : l3 : l1/x",
            "ERROR: Cannot open output file : errno=20 : Not a directory : ./l1/f.txt",
        ]).Should().BeFalse();
    }

    // hop/evil.txt is extracted on its own into a real hop/ folder and viahop is dropped when the links are
    // placed (7z is never asked to create either link); the archive extracts and nothing is written outside.
    [Fact]
    public async Task LinkThroughAnotherLinkIsDroppedAndTheRestExtracts()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var archive = outside.Path.Combine("through-link.zip");
        await using (var stream = File.Create(archive.ToString()))
        await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Add(string name, string content, int unixMode)
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = unixMode << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            Add("d/inner.txt", "inner content", 0x81A4);
            Add("hop", "d", 0xA1FF);
            Add("hop/evil.txt", "written through the link", 0x81A4);
            Add("viahop", "hop/inner.txt", 0xA1FF);
            Add("ok.txt", "fine", 0x81A4);
        }

        var destination = outside.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        var root = destination.ToString();
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray();
        entries.Where(e => new FileInfo(e).LinkTarget is not null).Should().BeEmpty();
        File.ReadAllText(Path.Combine(root, "ok.txt")).Should().Be("fine");
        File.ReadAllText(Path.Combine(root, "d", "inner.txt")).Should().Be("inner content");
        Directory.EnumerateFileSystemEntries(outside.Path.ToString()).Select(Path.GetFileName).Order()
            .Should().Equal("extracted", "through-link.zip");
    }

    // Both link names are non-UTF-8 and end in a space, so 7z cannot select them by name and they stay in the
    // main run. There 7z creates "hop\xff " but refuses "via\xfe " (its target runs through another link),
    // prints "Dangerous link via another link was ignored" and leaves a 0-byte regular file in its place.
    // That file must not be imported as the link's content: the result is a copy of d/inner.txt or nothing.
    [Fact]
    public async Task ARefusedLinkThatCannotBeNamedDoesNotLeaveAnEmptyFile()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var archive = outside.Path.Combine("unnameable-links.tar.gz");
        await File.WriteAllBytesAsync(archive.ToString(), Gzip(BuildTar(
            (Name: "d/inner.txt"u8.ToArray(), Content: "inner content"u8.ToArray(), LinkTarget: (byte[]?)null),
            (Name: [(byte)'h', (byte)'o', (byte)'p', 0xFF, (byte)' '], Content: [], LinkTarget: "d"u8.ToArray()),
            (Name: [(byte)'v', (byte)'i', (byte)'a', 0xFE, (byte)' '], Content: [], LinkTarget: [(byte)'h', (byte)'o', (byte)'p', 0xFF, (byte)' ', .. "/inner.txt"u8]),
            (Name: "ok.txt"u8.ToArray(), Content: "fine"u8.ToArray(), LinkTarget: null))), TestContext.Current.CancellationToken);

        var destination = outside.Path.Combine("extracted");
        destination.CreateDirectory();
        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        var root = destination.ToString();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        files.Where(f => new FileInfo(f).LinkTarget is not null).Should().BeEmpty();
        File.ReadAllText(Path.Combine(root, "ok.txt")).Should().Be("fine");
        File.ReadAllText(Path.Combine(root, "d", "inner.txt")).Should().Be("inner content");
        foreach (var file in files.Where(f => Path.GetFileName(f).StartsWith("via", StringComparison.Ordinal)))
        {
            File.ReadAllText(file).Should().Be("inner content", "an empty placeholder must not stand in for the link");
        }
    }

    // The link's name is not UTF-8 and ends in a space, so 7z cannot be told to leave it out and writes it itself, over
    // the link the destination already holds at that name. ExtractedNames.Repair unlinks it at once. 7z rewrites an
    // absolute target to sit under the destination, so no target here can start with the outside folder; what is pinned is
    // that no link from the archive survives at all (the sweep alone is covered in ExtractedLinksTests).
    [Fact]
    public async Task AnUnaddressableSymlinkOverAnExistingLinkDoesNotLeaveALinkOutside()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var outside = scratch.Path.Combine("outside");
        outside.CreateDirectory();
        var secret = outside.Combine("secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);

        byte[] linkName = [(byte)'h', (byte)'o', (byte)'p', 0xFF, (byte)' '];
        var archive = scratch.Path.Combine("unaddressable.tar.gz");
        await File.WriteAllBytesAsync(archive.ToString(), Gzip(BuildTar(
            (Name: linkName, Content: [], LinkTarget: System.Text.Encoding.UTF8.GetBytes(secret.ToString())),
            (Name: "ok.txt"u8.ToArray(), Content: "fine"u8.ToArray(), LinkTarget: null))), TestContext.Current.CancellationToken);

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        RawSymlink.Create(destination.ToString(), linkName, "harmless-target");

        try
        {
            await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

            File.ReadAllText(Path.Combine(destination.ToString(), "ok.txt")).Should().Be("fine");
            RawSymlink.Targets(destination.ToString()).Should().BeEmpty("a link whose name cannot be addressed is dropped, not left pointing anywhere");
            File.ReadAllText(secret.ToString()).Should().Be("private");
        }
        finally
        {
            // A name that is not UTF-8 cannot be deleted by the temporary folder's cleanup.
            RawSymlink.RemoveAll(destination.ToString());
        }
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(bytes);
        return output.ToArray();
    }

    // A minimal ustar writer, because TarWriter can only write names that are valid UTF-8.
    private static byte[] BuildTar(params (byte[] Name, byte[] Content, byte[]? LinkTarget)[] entries)
    {
        using var output = new MemoryStream();
        foreach (var (name, content, linkTarget) in entries)
        {
            var header = new byte[512];
            void Put(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(header.AsSpan(offset));
            Put(0, name);
            Put(100, System.Text.Encoding.ASCII.GetBytes(linkTarget is null ? "0000644" : "0000777"));
            Put(108, "0000000"u8);
            Put(116, "0000000"u8);
            Put(124, System.Text.Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0')));
            Put(136, "00000000000"u8);
            header.AsSpan(148, 8).Fill((byte)' ');
            header[156] = linkTarget is null ? (byte)'0' : (byte)'2';
            if (linkTarget is not null) Put(157, linkTarget);
            Put(257, "ustar\0"u8);
            Put(263, "00"u8);
            Put(148, System.Text.Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => b), 8).PadLeft(6, '0') + "\0 "));
            output.Write(header);
            output.Write(content);
            output.Write(new byte[(512 - content.Length % 512) % 512]);
        }

        output.Write(new byte[1024]);
        return output.ToArray();
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

    // The inner tar is found by what the gzip layer produced, not by what the destination holds, so a destination that
    // already has files in it is no different from an empty one.
    [Fact]
    public async Task ATarGzIntoAFolderThatAlreadyHoldsEntriesExtractsItsMembersAndLeavesThoseAlone()
    {
        await using var scratch = TemporaryFileManager.CreateFolder();
        var archive = scratch.Path.Combine("release.tar.gz");
        WriteTarGz(archive, ("bin/me3", UnixFileMode.UserRead | UnixFileMode.UserWrite), ("readme.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite));

        var destination = scratch.Path.Combine("extracted");
        destination.CreateDirectory();
        var oldFile = destination.Combine("old.txt").ToString();
        File.WriteAllText(oldFile, "old content");
        var oldLink = destination.Combine("old-link").ToString();
        File.CreateSymbolicLink(oldLink, "old.txt");

        await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);

        File.ReadAllText(destination.Combine("bin/me3").ToString()).Should().Be("bin/me3");
        File.ReadAllText(destination.Combine("readme.txt").ToString()).Should().Be("readme.txt");
        File.Exists(destination.Combine("release.tar").ToString()).Should().BeFalse("the inner tar is not a result");
        File.ReadAllText(oldFile).Should().Be("old content");
        new FileInfo(oldLink).LinkTarget.Should().Be("old.txt");
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
