using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.CrossPlatform;
using NexusMods.Paths;
using SilverModManager.Sdk.FileExtractor;

namespace SilverModManager.Sdk.Tests;

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
    public void Test_RunProcessLogFilesLiveBesideTheOthers()
    {
        var fs = NewFileSystem();
        var logs = fs.GetKnownPath(KnownPath.XDG_STATE_HOME).Combine("SilverModManager/Logs");
        var settings = LoggingSettings.CreateDefault();

        settings.RunProcessLogFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.run.current.log"));
        settings.RunProcessArchiveFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.run.{##}.log"));
    }

    [Fact]
    public void Test_ASettingsFileSavedBeforeTheRunLogsExistedStillGetsTheirDefaultPaths()
    {
        var fs = NewFileSystem();
        var logs = fs.GetKnownPath(KnownPath.XDG_STATE_HOME).Combine("SilverModManager/Logs");
        var saved = LoadSavedWithout(LoggingSettings.CreateDefault(), nameof(LoggingSettings.RunProcessLogFilePath), nameof(LoggingSettings.RunProcessArchiveFilePath));

        saved.RunProcessLogFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.run.current.log"));
        saved.RunProcessArchiveFilePath.ToPath(fs).Should().Be(logs.Combine("silvermodmanager.run.{##}.log"));
    }

    // The settings backend deserializes the saved file straight into the record, so this is what a file written by an
    // older build turns into.
    private static LoggingSettings LoadSavedWithout(LoggingSettings settings, params string[] properties)
    {
        var json = JsonSerializer.SerializeToNode(settings)!.AsObject();
        foreach (var property in properties) json.Remove(property).Should().BeTrue();
        return JsonSerializer.Deserialize<LoggingSettings>(json.ToJsonString())!;
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
