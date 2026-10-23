using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Games.FromSoft.EldenRing;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Tests.Secrets;

public class CoopPasswordMigrationTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly InMemorySecretStore _store = new();
    private readonly CoopPasswordMigration _step;

    public CoopPasswordMigrationTests() => _step = new CoopPasswordMigration(_fileSystem, NullLogger<CoopPasswordMigration>.Instance);

    private AbsolutePath SettingsFile => _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME)
                                         / ApplicationConstants.DataDirectoryName / "Configs"
                                         / $"{SettingsIds.For<FromSoftSettings>(null, '_')}.json";

    private async Task WriteSettings(string json)
    {
        SettingsFile.Parent.CreateDirectory();
        await SettingsFile.WriteAllTextAsync(json);
    }

    private async Task<JsonElement> ReadSettings()
    {
        using var document = JsonDocument.Parse(await SettingsFile.ReadAllTextAsync());
        return document.RootElement.Clone();
    }

    private const string WithPassword = """{"DataRoot":{"BaseKnownPath":"XDG_DATA_HOME","Path":"SilverModManager/FromSoft"},"CoopPassword":"hunter2","Count":3,"Flag":true,"Nested":{"a":[1,2]}}""";

    [Fact]
    public async Task MovesThePasswordIntoTheStore()
    {
        await WriteSettings(WithPassword);

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await _store.GetAsync(FromSoftSecretKeys.CoopPassword, CancellationToken.None)).Should().Be(new SecretResult.Found("hunter2"));
    }

    [Fact]
    public async Task TheRewrittenFileKeepsEveryOtherPropertyAndLosesThePassword()
    {
        await WriteSettings(WithPassword);

        await _step.RunAsync(_store, CancellationToken.None);

        var after = await ReadSettings();
        after.TryGetProperty("CoopPassword", out _).Should().BeFalse();
        var expected = JsonNode.Parse(WithPassword)!.AsObject();
        expected.Remove("CoopPassword");
        JsonNode.DeepEquals(JsonNode.Parse(after.GetRawText()), expected).Should().BeTrue();
        (await SettingsFile.ReadAllTextAsync()).Should().NotContain("hunter2");
    }

    [Fact]
    public async Task RerunFindsNothingToMove()
    {
        await WriteSettings(WithPassword);
        await _step.RunAsync(_store, CancellationToken.None);

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Fact]
    public async Task NoFileMeansNothingToMove()
    {
        (await _step.RunAsync(_store, CancellationToken.None)).Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Theory]
    [InlineData("""{"Count":3}""")]
    [InlineData("""{"CoopPassword":"","Count":3}""")]
    [InlineData("""{"CoopPassword":null,"Count":3}""")]
    public async Task AnAbsentOrEmptyPasswordIsNothingToMove(string json)
    {
        await WriteSettings(json);

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
        (await _store.GetAsync(FromSoftSecretKeys.CoopPassword, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task AnUnavailableStoreLeavesTheFileUntouched()
    {
        await WriteSettings(WithPassword);
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().Be(new SecretMigrationOutcome.Deferred("the wallet is locked"));
        (await SettingsFile.ReadAllTextAsync()).Should().Be(WithPassword);
    }
}
