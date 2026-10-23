using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Backend.Tests;

public class JsonStorageBackendLoggingTests
{
    private const string Secret = "hunter2-do-not-log";

    public record LeakySettings : ISettings
    {
        // Throws both ways, so one type drives the serialize and the deserialize failure with a secret in reach of the log.
        [JsonConverter(typeof(ThrowingConverter))]
        public string Password { get; init; } = Secret;

        public static string SettingsId => "Tests.LeakySettings";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    private sealed class ThrowingConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new JsonException("cannot read");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => throw new JsonException("cannot write");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The exception is logged beside the message; include both as a sink would.
            Messages.Add(formatter(state, exception) + " " + exception);
        }
    }

    private static (JsonStorageBackend Backend, CapturingLogger<JsonStorageBackend> Logger, IFileSystem FileSystem) Create()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var logger = new CapturingLogger<JsonStorageBackend>();
        var services = new ServiceCollection()
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton(new JsonSerializerOptions())
            .BuildServiceProvider();
        return (new JsonStorageBackend(logger, services), logger, fileSystem);
    }

    [Fact]
    public void ASerializeFailureNamesTheTypeAndTheFileAndNotTheValue()
    {
        var (backend, logger, fileSystem) = Create();

        backend.Save(new LeakySettings(), key: null);

        var file = JsonStorageBackend.GetConfigsFolderPath(fileSystem) / $"{LeakySettings.SettingsId}.json";
        logger.Messages.Should().ContainSingle().Which.Should().Contain(nameof(LeakySettings)).And.Contain(file.GetFullPath()).And.NotContain(Secret);
    }

    [Fact]
    public async Task ADeserializeFailureNamesTheTypeAndTheFileAndNotTheJson()
    {
        var (backend, logger, fileSystem) = Create();
        var file = JsonStorageBackend.GetConfigsFolderPath(fileSystem) / $"{LeakySettings.SettingsId}.json";
        await file.WriteAllTextAsync($$"""{"Password":"{{Secret}}"}""");

        var loaded = backend.Load<LeakySettings>(key: null);

        loaded.Should().BeNull();
        logger.Messages.Should().ContainSingle().Which.Should().Contain(nameof(LeakySettings)).And.Contain(file.GetFullPath()).And.NotContain(Secret);
    }
}
