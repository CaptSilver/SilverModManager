using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FileHashes;
using NexusMods.Paths;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

public class DataDirectoryTests
{
    [Fact]
    public void Test_SyncFileUsesProductName()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var provider = new ServiceCollection().AddSingleton<IFileSystem>(fs).BuildServiceProvider();

        CliSettings.CreateDefault(provider).SyncFile
            .Should().Be(fs.GetKnownPath(KnownPath.XDG_RUNTIME_DIR).Combine("SilverModManager-sync_file.sync"));
    }

    [Fact]
    public void Test_HashDatabaseLivesUnderProductDataFolder()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);

        FileHashesServiceSettings.CreateDefault(serviceProvider: null!).HashDatabaseLocation.ToPath(fs)
            .Should().Be(fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("SilverModManager/FileHashesDatabase"));
    }
}
