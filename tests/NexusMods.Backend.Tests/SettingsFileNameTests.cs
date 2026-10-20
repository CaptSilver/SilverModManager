using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Backend.Tests;

public class SettingsFileNameTests
{
    public record SampleSettings : ISettings
    {
        public string Value { get; init; } = "";
        public static string SettingsId => "SilverModManager.Tests.SampleSettings";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    private static (JsonStorageBackend Backend, InMemoryFileSystem Fs) CreateBackend()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var services = new ServiceCollection()
            .AddSingleton<IFileSystem>(fs)
            .AddSingleton(new JsonSerializerOptions())
            .BuildServiceProvider();
        return (new JsonStorageBackend(NullLogger<JsonStorageBackend>.Instance, services), fs);
    }

    [Fact]
    public void Test_JsonFileIsNamedAfterSettingsId()
    {
        var (backend, fs) = CreateBackend();
        var configs = JsonStorageBackend.GetConfigsFolderPath(fs);

        backend.Save(new SampleSettings { Value = "a" }, key: null);
        backend.Save(new SampleSettings { Value = "b" }, key: "k");

        configs.Combine("SilverModManager.Tests.SampleSettings.json").FileExists.Should().BeTrue();
        configs.Combine("SilverModManager.Tests.SampleSettings_k.json").FileExists.Should().BeTrue();
        backend.Load<SampleSettings>("k")!.Value.Should().Be("b");
    }
}
