using FluentAssertions;
using NexusMods.App.UI.Pages.Wabbajack;
using NexusMods.Paths;

namespace NexusMods.UI.Tests.Pages;

public class WabbajackInboxCopyTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared.FromUnsanitizedFullPath(Directory.CreateTempSubdirectory("wabbajack-inbox-").FullName);

    public void Dispose() => Directory.Delete(_root.ToString(), recursive: true);

    private AbsolutePath Folder(string name)
    {
        var folder = _root.Combine(name);
        folder.CreateDirectory();
        return folder;
    }

    [Fact]
    public async Task ACopiedFile_LandsInTheInboxUnderItsOwnName()
    {
        var source = Folder("downloads").Combine("mod.7z");
        await source.WriteAllTextAsync("archive bytes");
        var inbox = Folder("inbox");

        await WabbajackModlistService.CopyIntoInbox(source, inbox, CancellationToken.None);

        (await inbox.Combine("mod.7z").ReadAllTextAsync()).Should().Be("archive bytes");
        source.FileExists.Should().BeTrue("the user's own copy stays where it was");
        inbox.EnumerateFiles().Should().ContainSingle("no partial file is left behind");
    }

    [Fact]
    public async Task AFileAlreadyInTheInbox_KeepsItsBytes()
    {
        // Dropping a file from the opened inbox folder back onto the page names the same file as source and target.
        var inbox = Folder("inbox");
        var file = inbox.Combine("mod.7z");
        await file.WriteAllTextAsync("archive bytes");

        await WabbajackModlistService.CopyIntoInbox(file, inbox, CancellationToken.None);

        (await file.ReadAllTextAsync()).Should().Be("archive bytes");
    }
}
