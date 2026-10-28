using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout create --large-files</c> over a game folder with more added files than the backup cap (1 KB here).
/// The verb never reaches the prompt: a verb forwarded to the running window would open a dialog on the desktop.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutCreateLargeFilesTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutCreateLargeFilesTests, EldenRingGame>(helper)
{
    private readonly ThrowingPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddFromSoftGames()
            .AddSingleton(new BackupLimit(Size.KB))
            .AddSingleton<IBackupOverflowPrompt>(_prompt)
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        return services;
    }

    private sealed class ThrowingPrompt : IBackupOverflowPrompt
    {
        public int Asked;

        public ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
        {
            Asked++;
            throw new InvalidOperationException("a verb must not open the prompt");
        }
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    private async Task WriteAddedFiles()
    {
        await GenerateGameFiles();
        var folder = GameInstallation.LocatorResult.Path;
        for (var i = 0; i < 4; i++) File.WriteAllBytes((folder / $"added{i}.bin").ToString(), new byte[4096]);
    }

    private int Visible => Loadout.All(Connection.Db).Count(l => l.IsVisible());

    [Fact]
    public async Task WithoutTheFlag_RefusesNamingTheFlagAndNeverAsks()
    {
        await WriteAddedFiles();

        var (exit, output) = await Run("loadout", "create", "-g", EldenRingGame.GameId.ToString());

        exit.Should().NotBe(0);
        output.Should().Contain("--large-files");
        _prompt.Asked.Should().Be(0);
        Visible.Should().Be(0);
    }

    [Theory]
    [InlineData("adopt")]
    [InlineData("backup")]
    public async Task WithTheFlag_CreatesTheLoadoutWithoutAsking(string value)
    {
        await WriteAddedFiles();

        var (exit, output) = await Run("loadout", "create", "-g", EldenRingGame.GameId.ToString(), "--large-files", value);

        exit.Should().Be(0, output);
        _prompt.Asked.Should().Be(0);
        Visible.Should().Be(1);
    }

    [Fact]
    public async Task WithAnUnknownValue_RefusesNamingTheValues()
    {
        await WriteAddedFiles();

        var (exit, output) = await Run("loadout", "create", "-g", EldenRingGame.GameId.ToString(), "--large-files", "sometimes");

        exit.Should().NotBe(0);
        output.Should().Contain("adopt").And.Contain("backup");
        Visible.Should().Be(0);
    }
}
