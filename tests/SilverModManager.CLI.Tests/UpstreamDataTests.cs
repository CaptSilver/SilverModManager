using FluentAssertions;
using NexusMods.App;
using NexusMods.Paths;

namespace NexusMods.CLI.Tests;

public class UpstreamDataTests
{
    [Fact]
    public void Test_NotFoundWhenUpstreamFolderIsMissing()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);

        UpstreamData.Exists(fs).Should().BeFalse();
    }

    [Fact]
    public void Test_FoundWhenUpstreamDataModelFolderExists()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var dataModel = fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("NexusMods.App/DataModel");
        dataModel.CreateDirectory();

        UpstreamData.DataModelPath(fs).Should().Be(dataModel);
        UpstreamData.Exists(fs).Should().BeTrue();
    }

    [Fact]
    public void Test_OwnDataFolderIsNotMistakenForUpstream()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("SilverModManager/DataModel").CreateDirectory();

        UpstreamData.Exists(fs).Should().BeFalse();
    }
}
