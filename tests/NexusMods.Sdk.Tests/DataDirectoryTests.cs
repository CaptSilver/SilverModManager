using FluentAssertions;
using NexusMods.CrossPlatform;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;

namespace NexusMods.Sdk.Tests;

public class DataDirectoryTests
{
    private static InMemoryFileSystem NewFileSystem() => new(OSInformation.FakeUnix);

    [Fact]
    public void Test_LogFilesLiveUnderStateHome()
    {
        var fs = NewFileSystem();
        var logs = fs.GetKnownPath(KnownPath.XDG_STATE_HOME).Combine("SilverModManager/Logs");
        var settings = LoggingSettings.CreateDefault();

        LoggingSettings.GetLogBaseFolder(fs).Should().Be(logs);
        settings.MainProcessLogFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.main.current.log"));
        settings.MainProcessArchiveFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.main.{##}.log"));
        settings.SlimProcessLogFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.slim.current.log"));
        settings.SlimProcessArchiveFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.slim.{##}.log"));
    }

    [Fact]
    public void Test_ExtractorTempFolderLivesUnderStateHome()
    {
        var fs = NewFileSystem();
        var settings = FileExtractorSettings.CreateDefault(serviceProvider: null!);

        settings.TempFolderLocation.ToPath(fs)
            .Should().Be(fs.GetKnownPath(KnownPath.XDG_STATE_HOME).Combine("SilverModManager/Temp"));
    }
}
