using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine;
using NexusMods.Games.CreationEngine.Fallout76;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

/// <summary>
/// <c>silvermod add-game</c> against made-up game folders. The host records into the in-memory store, so no
/// installs.yaml is read or written.
/// </summary>
[Collection("ConsoleOutput")]
public class AddGameVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<AddGameVerbTests, Fallout76>(helper)
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "add-game-verb-" + Guid.NewGuid().ToString("N"));

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<Fallout76>(new Version("1.0"))
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();

    public override ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return base.DisposeAsync();
    }

    private IUserGameInstallStore Store => ServiceProvider.GetRequiredService<IUserGameInstallStore>();

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return folder;
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    [Fact]
    public async Task AWindowsGameWithAPrefixIsAddedAndRecorded()
    {
        var folder = Folder("fo76", "Fallout76.exe");
        var prefix = Folder("prefix");

        var (exitCode, output) = await Run("add-game", "-f", folder, "-p", prefix);

        exitCode.Should().Be(0, output);
        output.Should().Contain("Added Fallout 76");
        Store.GetAll().Should().ContainSingle().Which.Should().Match<UserGameInstall>(install =>
            install.Game == Fallout76.GameId && install.Path == folder && install.Prefix == prefix);
    }

    [Fact]
    public async Task AWindowsGameWithoutAPrefixIsRefusedAndNothingIsRecorded()
    {
        var (exitCode, output) = await Run("add-game", "-f", Folder("fo76", "Fallout76.exe"));

        exitCode.Should().Be(1);
        output.Should().Contain("Fallout 76 is a Windows build; pass --prefix <wine prefix> so it can run");
        Store.GetAll().Should().BeEmpty();
    }

    // Enderal launches TESV.exe too, but it names files a Skyrim folder lacks, so the executable alone is Skyrim.
    [Fact]
    public async Task SkyrimsExecutableAloneIsSkyrimNotEnderal()
    {
        var (exitCode, output) = await Run("add-game", "-f", Folder("tesv", "TESV.exe"), "-p", Folder("prefix"));

        exitCode.Should().Be(0, output);
        output.Should().Contain("Added Skyrim");
        Store.GetAll().Should().ContainSingle().Which.Game.Should().Be(NexusMods.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId);
    }

    [Fact]
    public async Task EnderalsFilesMakeTheSameExecutableEnderal()
    {
        var folder = Folder("enderal", "TESV.exe", "Enderal Launcher.exe", "Data/Enderal - Forgotten Stories.esm");

        var (exitCode, output) = await Run("add-game", "-f", folder, "-p", Folder("prefix"));

        exitCode.Should().Be(0, output);
        output.Should().Contain("Added Enderal");
        Store.GetAll().Should().ContainSingle().Which.Game.Should().Be(NexusMods.Games.CreationEngine.Enderal.Enderal.GameId);
    }

    // The folder alone says Skyrim, so only an honoured --game gives Enderal: the game id is applied before the
    // identifying files are checked.
    [Fact]
    public async Task AGameArgumentPicksBetweenCandidates()
    {
        var (exitCode, output) = await Run("add-game", "-f", Folder("tesv", "TESV.exe"), "-g", "CreationEngine.Enderal", "-p", Folder("prefix"));

        exitCode.Should().Be(0, output);
        output.Should().Contain("Added Enderal");
        Store.GetAll().Should().ContainSingle().Which.Game.Should().Be(NexusMods.Games.CreationEngine.Enderal.Enderal.GameId);
    }

    [Fact]
    public async Task AFolderNoGameLaunchesFromIsRefused()
    {
        var (exitCode, output) = await Run("add-game", "-f", Folder("empty"));

        exitCode.Should().Be(1);
        output.Should().Contain("No known game launches from");
        Store.GetAll().Should().BeEmpty();
    }
}
