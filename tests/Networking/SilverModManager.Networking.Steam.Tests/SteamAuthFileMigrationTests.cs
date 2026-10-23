using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.Steam.DTOs;
using NexusMods.Networking.Steam.Migration;
using NexusMods.Paths;
using NexusMods.Sdk;
using Xunit;

namespace NexusMods.Networking.Steam.Tests;

public class SteamAuthFileMigrationTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly InMemorySecretStore _store = new();
    private readonly SteamAuthFileMigration _step;

    public SteamAuthFileMigrationTests() => _step = new SteamAuthFileMigration(_fileSystem, NullLogger<SteamAuthFileMigration>.Instance);

    private AbsolutePath OldFile => _fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory) / ApplicationConstants.DataDirectoryName / "steam/auth";

    private async Task WriteOldFile(byte[] bytes)
    {
        OldFile.Parent.CreateDirectory();
        await OldFile.WriteAllBytesAsync(bytes);
    }

    [Fact]
    public async Task MovesTheFileIntoTheStore()
    {
        var bytes = new AuthData { Username = "user", RefreshToken = "refresh" }.Save();
        await WriteOldFile(bytes);

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        var stored = (await _store.GetAsync(SteamSecretKeys.Auth, CancellationToken.None)).Should().BeOfType<SecretResult.Found>().Subject;
        stored.Value.Should().Be(Encoding.UTF8.GetString(bytes));
        OldFile.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task RerunFindsNothingToMove()
    {
        await WriteOldFile(new AuthData { Username = "user", RefreshToken = "refresh" }.Save());
        await _step.RunAsync(_store, CancellationToken.None);

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Fact]
    public async Task LeavesTheFileWhenTheStoreIsUnavailable()
    {
        await WriteOldFile(new AuthData { Username = "user", RefreshToken = "refresh" }.Save());
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var outcome = await _step.RunAsync(_store, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.Deferred>().Which.Reason.Should().Contain("the wallet is locked");
        OldFile.FileExists.Should().BeTrue();
    }
}
