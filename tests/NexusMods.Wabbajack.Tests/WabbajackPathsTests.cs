using FluentAssertions;
using NexusMods.DataModel;
using NexusMods.Paths;

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
}
