using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Backend.Tests;

public class ConfigsFolderTests
{
    [Fact]
    public void Test_ConfigsLiveUnderProductDataFolder()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);

        JsonStorageBackend.GetConfigsFolderPath(fs)
            .Should().Be(fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("SilverModManager/Configs"));
    }
}
