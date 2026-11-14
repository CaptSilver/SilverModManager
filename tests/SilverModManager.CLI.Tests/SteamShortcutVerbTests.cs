using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.Commandline;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermodmanager steam-shortcut status|add|remove</c> against a substituted <see cref="ISteamShortcutService"/>.
/// </summary>
public class SteamShortcutVerbTests
{
    private readonly ISteamShortcutService _service = Substitute.For<ISteamShortcutService>();

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddSingleton<NexusMods.Paths.IFileSystem>(NexusMods.Paths.FileSystem.Shared)
            .AddSingleton(_service)
            .AddSteamShortcutVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Arguments is { Length: > 0 } a ? string.Format(t.Template, a) : t.Template)));
    }

    [Fact]
    public async Task StatusPrintsTheAccountAndAppId()
    {
        _service.Status(Arg.Any<uint?>()).Returns(new SteamShortcutStatus(SteamShortcutState.InSteam, new SteamAccount(65369667, "alice", "Alice"), 2147483650u, []));

        var (exitCode, output) = await Run("steam-shortcut", "status");

        exitCode.Should().Be(0);
        output.Should().Contain("Account: Alice (65369667)").And.Contain("AppId: 2147483650").And.Contain("State: InSteam");
    }

    [Fact]
    public async Task StatusWithAnUndecidedAccountListsTheCandidates()
    {
        _service.Status(Arg.Any<uint?>()).Returns(new SteamShortcutStatus(SteamShortcutState.AccountUndecided, null, null,
            [new SteamAccount(1, "alice", "Alice"), new SteamAccount(2, "bob", "Bob")]));

        var (_, output) = await Run("steam-shortcut", "status");

        output.Should().Contain("State: AccountUndecided").And.Contain("Alice (1)").And.Contain("Bob (2)").And.Contain("--account");
    }

    [Fact]
    public async Task AddPassesRouteAndAccountToTheService()
    {
        var (exitCode, _) = await Run("steam-shortcut", "add", "--route", "steam-url", "--account", "65369667");

        exitCode.Should().Be(0);
        await _service.Received(1).AddAsync("steam-url", 65369667u, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddThroughSteamDoesNotTellTheUserToStartSteam()
    {
        var (_, output) = await Run("steam-shortcut", "add", "--route", "steam-url");

        output.Should().Contain("Asked Steam to add it").And.NotContain("Start Steam");
    }

    [Fact]
    public async Task AddWithSteamClosedSaysToStartSteam()
    {
        var (_, output) = await Run("steam-shortcut", "add");

        output.Should().Contain("Added. Start Steam to see it.");
    }

    [Fact]
    public async Task AddDefaultsToTheFileRouteAndNoAccount()
    {
        await Run("steam-shortcut", "add");

        await _service.Received(1).AddAsync("file", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddWhileSteamRunsExitsNonZeroWithTheRefusal()
    {
        _service.AddAsync(Arg.Any<string>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new SteamShortcutRefusedException("Steam is running. Close it and try again.", removing: false));

        var (exitCode, output) = await Run("steam-shortcut", "add");

        exitCode.Should().NotBe(0);
        output.Should().Contain("Steam is running.");
    }

    [Fact]
    public async Task AnAccountIdPastTheIntRangeIsPassedThrough()
    {
        await Run("steam-shortcut", "status", "--account", "3000000000");

        _service.Received(1).Status(3000000000u);
    }

    [Fact]
    public async Task RemovePassesTheAccountToTheService()
    {
        var (exitCode, _) = await Run("steam-shortcut", "remove", "-a", "7");

        exitCode.Should().Be(0);
        await _service.Received(1).RemoveAsync(7u, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANonNumericAccountIsRefusedBeforeTheServiceIsCalled()
    {
        var (exitCode, _) = await Run("steam-shortcut", "remove", "--account", "alice");

        exitCode.Should().NotBe(0);
        await _service.DidNotReceiveWithAnyArgs().RemoveAsync(default, default);
    }
}
