using FluentAssertions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Games.FromSoft.EldenRing;

namespace NexusMods.Games.FromSoft.Tests.Secrets;

public class SeamlessCoopSettingsTransformTests : IDisposable
{
    private const string Template = "[PASSWORD]\ncooppassword = @COOP_PASSWORD@\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("coop-transform").FullName;
    private readonly InMemorySecretStore _store = new();

    private string Source => Path.Combine(_folder, "template.ini");
    private string Target => Path.Combine(_folder, "ersc_settings.ini");

    public SeamlessCoopSettingsTransformTests() => File.WriteAllText(Source, Template);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Task Write() => new SeamlessCoopSettingsTransform(_store).WriteAsync(Source, Target, CancellationToken.None);

    [Fact]
    public async Task ThePasswordFromTheStoreGoesIntoTheWrittenFile()
    {
        await _store.SetAsync(FromSoftSecretKeys.CoopPassword, "hunter2", CancellationToken.None);

        await Write();

        (await File.ReadAllTextAsync(Target)).Should().Be("[PASSWORD]\ncooppassword = hunter2\n");
    }

    [Fact]
    public async Task ANewlyCreatedFileIsPrivate()
    {
        await _store.SetAsync(FromSoftSecretKeys.CoopPassword, "hunter2", CancellationToken.None);

        await Write();

        File.GetUnixFileMode(Target).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task NoStoredPasswordWritesAnEmptyOne()
    {
        await Write();

        (await File.ReadAllTextAsync(Target)).Should().Be("[PASSWORD]\ncooppassword = \n");
    }

    [Fact]
    public async Task AnUnavailableStoreRefusesAndLeavesTheTargetAlone()
    {
        await File.WriteAllTextAsync(Target, "previous");
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var act = () => Write();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("The Seamless Co-op password could not be read (the wallet is locked); the settings file was not written so a session without the password is not started");
        (await File.ReadAllTextAsync(Target)).Should().Be("previous");
    }

    [Fact]
    public async Task AnUnavailableStoreDoesNotCreateAMissingTarget()
    {
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");

        var act = () => Write();

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Target).Should().BeFalse();
    }

    [Fact]
    public void FromSoftSettingsHoldsNoPassword()
    {
        typeof(FromSoftSettings).GetMembers().Select(m => m.Name).Should().NotContain("CoopPassword");
    }

    [Fact]
    public void TheKeyIsNamedAsThePersistedIdentifier()
    {
        FromSoftSecretKeys.CoopPassword.Name.Should().Be("fromsoft.ersc-password");
    }
}
