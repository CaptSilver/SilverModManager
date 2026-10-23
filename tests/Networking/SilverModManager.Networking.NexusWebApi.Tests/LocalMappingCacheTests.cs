using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.V1Interop;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class LocalMappingCacheTests
{
    [Fact]
    public void Test_Parse()
    {
        LocalMappingCache.TryParseJsonFile(NullLogger.Instance, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Test_Parse_DuplicateDomains_LeavesDomainToTheFallbackCache()
    {
        // Two game ids that share one domain name, as in the real games.json.
        const string json = """
        [
          { "id": 100, "domain_name": "morrowind" },
          { "id": 180, "domain_name": "neverwinter" },
          { "id": 8703, "domain_name": "neverwinter" }
        ]
        """;
        var logger = new RecordingLogger();

        LocalMappingCache.TryParse(new MemoryStream(Encoding.UTF8.GetBytes(json)), logger, out var idToDomain, out var domainToId)
            .Should().BeTrue();

        domainToId!.Keys.Should().BeEquivalentTo([GameDomain.From("morrowind")], "an ambiguous domain must not be guessed, the fallback cache resolves it");
        idToDomain![NexusModsGameId.From(180)].Should().Be(GameDomain.From("neverwinter"));
        idToDomain[NexusModsGameId.From(8703)].Should().Be(GameDomain.From("neverwinter"), "every id keeps its domain");
        idToDomain.Count.Should().Be(3);

        logger.Messages.Should().ContainSingle(m => m.Level == LogLevel.Warning && m.Text.Contains("neverwinter"));
    }

    [Fact]
    public void Test_Parse_DuplicateIds_LeavesIdToTheFallbackCache()
    {
        const string json = """
        [
          { "id": 100, "domain_name": "morrowind" },
          { "id": 200, "domain_name": "first" },
          { "id": 200, "domain_name": "second" }
        ]
        """;
        var logger = new RecordingLogger();

        LocalMappingCache.TryParse(new MemoryStream(Encoding.UTF8.GetBytes(json)), logger, out var idToDomain, out var domainToId)
            .Should().BeTrue();

        idToDomain!.Keys.Should().BeEquivalentTo([NexusModsGameId.From(100)]);
        domainToId!.Count.Should().Be(3, "each domain still has exactly one id");
        logger.Messages.Should().ContainSingle(m => m.Level == LogLevel.Warning && m.Text.Contains("200"));
    }

    [Fact]
    public void Test_Parse_RepeatedIdenticalEntry_IsNotAmbiguous()
    {
        const string json = """
        [
          { "id": 100, "domain_name": "morrowind" },
          { "id": 100, "domain_name": "morrowind" }
        ]
        """;
        var logger = new RecordingLogger();

        LocalMappingCache.TryParse(new MemoryStream(Encoding.UTF8.GetBytes(json)), logger, out var idToDomain, out var domainToId)
            .Should().BeTrue();

        idToDomain!.Count.Should().Be(1);
        domainToId!.Count.Should().Be(1);
        logger.Messages.Should().NotContain(m => m.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Test_Parse_InvalidJson_LogsError()
    {
        var logger = new RecordingLogger();

        LocalMappingCache.TryParse(new MemoryStream("not json"u8.ToArray()), logger, out _, out _).Should().BeFalse();

        logger.Messages.Should().Contain(m => m.Level == LogLevel.Error);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
