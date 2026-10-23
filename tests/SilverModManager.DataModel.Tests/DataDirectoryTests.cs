using FluentAssertions;
using NexusMods.Paths;

namespace NexusMods.DataModel.Tests;

public class DataDirectoryTests
{
    [Fact]
    public void Test_DataModelLivesUnderProductDataFolder()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var expected = fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("SilverModManager");

        DataModelSettings.GetLocalApplicationDataDirectory(fs).Should().Be(expected);
        DataModelSettings.GetStandardDataModelFolder(fs).Parent.Should().Be(expected);
    }
}
