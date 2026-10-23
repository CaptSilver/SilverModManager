using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Networking.Steam.DTOs;
using SilverModManager.Networking.Steam.Exceptions;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class SteamLoginTests
{
    private readonly List<string> _events = [];
    private readonly FakeAuthenticator _authenticator;
    private readonly FakeStorage _storage = new();
    private Dictionary<string, string> _environment = new();

    public SteamLoginTests() => _authenticator = new FakeAuthenticator(_events);

    private SteamLogin Login() => new(_storage, _authenticator, NullLogger.Instance, name => _environment.GetValueOrDefault(name));

    [Fact]
    public async Task SavedLoginLogsOnWithoutAuthenticating()
    {
        _storage.Load = new AuthLoadResult.Loaded(new AuthData { Username = "user", RefreshToken = "token" }.Save());

        await Login().RunAsync();

        _events.Should().Equal("LogOn user");
    }

    [Fact]
    public async Task UnavailableStoreFailsTheLoginWithoutStartingAnAuthSession()
    {
        _storage.Load = new AuthLoadResult.Unavailable("the wallet is locked");

        var act = () => Login().RunAsync();

        (await act.Should().ThrowAsync<SteamLoginException>()).Which.Message.Should().Contain("the wallet is locked");
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task QrLoginLogsOnBeforeSaving()
    {
        _storage.Load = new AuthLoadResult.None();
        _storage.OnSave = () => _events.Add("Save");

        await Login().RunAsync();

        _events.Should().Equal("Qr", "LogOn qr-user", "Save");
        AuthData.Load(_storage.Saved!).RefreshToken.Should().Be("qr-token");
    }

    [Fact]
    public async Task PasswordLoginLogsOnBeforeSaving()
    {
        _storage.Load = new AuthLoadResult.None();
        _storage.OnSave = () => _events.Add("Save");
        _environment = new() { ["STEAM_USER"] = "pw-user", ["STEAM_PASS"] = "secret" };

        await Login().RunAsync();

        _events.Should().Equal("Password pw-user", "LogOn pw-user", "Save");
    }

    [Fact]
    public async Task FailedSaveLeavesTheSessionLoggedOn()
    {
        _storage.Load = new AuthLoadResult.None();
        _storage.OnSave = () => throw new InvalidOperationException("the wallet is locked");

        var act = () => Login().RunAsync();

        await act.Should().NotThrowAsync();
        _events.Should().Equal("Qr", "LogOn qr-user");
    }

    private sealed class FakeAuthenticator(List<string> events) : ISteamAuthenticator
    {
        public Task<SteamCredentials> AuthenticateWithPasswordAsync(string username, string password)
        {
            events.Add($"Password {username}");
            return Task.FromResult(new SteamCredentials(username, "pw-token"));
        }

        public Task<SteamCredentials> AuthenticateWithQrAsync()
        {
            events.Add("Qr");
            return Task.FromResult(new SteamCredentials("qr-user", "qr-token"));
        }

        public void LogOn(SteamCredentials credentials) => events.Add($"LogOn {credentials.AccountName}");
    }

    private sealed class FakeStorage : IAuthStorage
    {
        public AuthLoadResult Load { get; set; } = new AuthLoadResult.None();
        public Action? OnSave { get; set; }
        public byte[]? Saved { get; private set; }

        public Task<AuthLoadResult> TryLoadAsync(CancellationToken cancellationToken) => Task.FromResult(Load);

        public Task SaveAsync(byte[] data, CancellationToken cancellationToken)
        {
            Saved = data;
            OnSave?.Invoke();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
