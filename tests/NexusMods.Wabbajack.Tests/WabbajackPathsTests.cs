using FluentAssertions;
using NexusMods.DataModel;
using NexusMods.Paths;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Tests;

public class WabbajackPathsTests(WabbajackPaths paths, IFileSystem fileSystem)
{
    private AbsolutePath TestRoot => fileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine(Startup.SettingsRoot);

    [Fact]
    public void BothFoldersResolveUnderTheTestRootAndAreCreated()
    {
        paths.Inbox.Should().Be(TestRoot.Combine("Inbox"));
        paths.ExtrasRoot.Should().Be(TestRoot.Combine("Extras"));
        paths.Inbox.DirectoryExists().Should().BeTrue();
        paths.ExtrasRoot.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void NeitherFolderIsUnderTheRealDataDirectory()
    {
        var dataDirectory = DataModelSettings.GetLocalApplicationDataDirectory(fileSystem);

        paths.Inbox.InFolder(dataDirectory).Should().BeFalse();
        paths.ExtrasRoot.InFolder(dataDirectory).Should().BeFalse();
    }

    [Fact]
    public void TheDefaultsLiveInAFolderOfTheirOwnUnderTheDataDirectory()
    {
        var defaults = WabbajackSettings.CreateDefault(null!);
        var dataDirectory = DataModelSettings.GetLocalApplicationDataDirectory(fileSystem);

        defaults.InboxPath.ToPath(fileSystem).Should().Be(dataDirectory.Combine("Wabbajack/Inbox"));
        defaults.ExtrasRoot.ToPath(fileSystem).Should().Be(dataDirectory.Combine("Wabbajack/Extras"));
    }

    [Theory]
    [InlineData("Test list", "Test list")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  ", "modlist")]
    public void AListsExtrasFolderIsNamedAfterItWithoutSeparatorsAndEndsWithTheGroupsId(string listName, string folderName)
    {
        var id = WabbajackModlistLoadoutGroupId.From(0x0100000000000042UL);
        var folder = paths.ExtrasFor(listName, id);

        folder.Should().Be(TestRoot.Combine("Extras").Combine($"{folderName}-{id.Value:x}"));
        folder.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void ListsWhoseNamesSanitiseToTheSameFolderStillGetFoldersOfTheirOwn()
    {
        var first = paths.ExtrasFor("a/b", WabbajackModlistLoadoutGroupId.From(0x0100000000000001UL));
        var second = paths.ExtrasFor("a:b", WabbajackModlistLoadoutGroupId.From(0x0100000000000002UL));

        first.Should().NotBe(second);
    }
}
