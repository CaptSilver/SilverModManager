using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout files unwritable</c>, <c>accept-disk</c> and <c>forget</c> list and settle the files an apply cannot
/// write, through <see cref="ISynchronizerService"/> like the Apply preview does.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutUnwritableFilesVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutUnwritableFilesVerbTests, EldenRingGame>(helper)
{
    private static readonly GamePath Kept = new(LocationId.Game, "mod/a/one.txt");
    private static readonly GamePath Gone = new(LocationId.Game, "mod/a/two.txt");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services
            .AddFromSoftGames()
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

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    /// <summary>
    /// An applied mod with two files. Its row for one.txt then wants bytes nobody kept while the game folder has the
    /// applied file, and its row for two.txt wants bytes nobody kept while the file is gone from the game folder.
    /// </summary>
    private async Task<Loadout.ReadOnly> LoadoutWithTwoUnwritableFiles()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"mod/a/one.txt", (RelativePath)"mod/a/two.txt"], loadout.LoadoutId, "Mod");
            await tx.Commit();
        }
        await ServiceProvider.GetRequiredService<ISynchronizerService>().Synchronize(loadout.LoadoutId);

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var row in LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
                tx.Add(row.Id, LoadoutFile.Hash, $"bytes nobody kept for {row.AsLoadoutItemWithTargetPath().TargetPath}".xxHash3AsUtf8());
            await tx.Commit();
        }
        (GameFolder(loadout) / "mod/a/two.txt").Delete();
        return Refresh(loadout);
    }

    private static AbsolutePath GameFolder(Loadout.ReadOnly loadout) => loadout.InstallationInstance.Locations[LocationId.Game].Path;

    private IReadOnlyList<UnwritableFile> Unwritable(Loadout.ReadOnly loadout) =>
        ServiceProvider.GetRequiredService<ISynchronizerService>().GetUnwritableFiles(loadout.LoadoutId);

    [Fact]
    public async Task Unwritable_ListsEachFileWithItsReason()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();

        var (exit, output) = await Run("loadout", "files", "unwritable", "-l", loadout.LoadoutId.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("mod/a/one.txt").And.Contain("mod/a/two.txt").And.Contain("no copy kept");
    }

    [Fact]
    public async Task AcceptDisk_SettlesTheFileOnDiskAndLeavesTheOthers()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();
        var kept = await (GameFolder(loadout) / "mod/a/one.txt").ReadAllBytesAsync();

        var (exit, output) = await Run("loadout", "files", "accept-disk", "-l", loadout.LoadoutId.ToString(), "-p", "mod/a/one.txt");

        exit.Should().Be(0, output);
        Unwritable(loadout).Select(f => f.Path).Should().BeEquivalentTo([Gone], "only the file asked for was settled");
        (await (GameFolder(loadout) / "mod/a/one.txt").ReadAllBytesAsync()).Should().Equal(kept);
    }

    [Fact]
    public async Task Forget_SettlesTheMissingFileAndItStaysGone()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();

        var (exit, output) = await Run("loadout", "files", "forget", "-l", loadout.LoadoutId.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("Settled").And.NotContain("Skipping", "with no paths, files the resolution does not fit are left alone, not reported");
        Unwritable(loadout).Select(f => f.Path).Should().Equal(Kept);
        await ServiceProvider.GetRequiredService<ISynchronizerService>().Synchronize(loadout.LoadoutId);
        (GameFolder(loadout) / "mod/a/two.txt").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task BothVerbsTogether_LeaveNothingPending()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();

        (await Run("loadout", "files", "accept-disk", "-l", loadout.LoadoutId.ToString())).ExitCode.Should().Be(0);
        (await Run("loadout", "files", "forget", "-l", loadout.LoadoutId.ToString())).ExitCode.Should().Be(0);

        Unwritable(loadout).Should().BeEmpty();
        var (_, listing) = await Run("loadout", "files", "unwritable", "-l", loadout.LoadoutId.ToString());
        listing.Should().Contain("No file is waiting");
    }

    [Fact]
    public async Task Forget_OfAFileThatIsOnDisk_IsRefusedAndChangesNothing()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();

        var (exit, _) = await Run("loadout", "files", "forget", "-l", loadout.LoadoutId.ToString(), "-p", "mod/a/one.txt");

        exit.Should().NotBe(0);
        Unwritable(loadout).Should().HaveCount(2);
    }

    [Fact]
    public async Task AcceptDisk_AcceptsAPathWithItsLocationInBraces()
    {
        var loadout = await LoadoutWithTwoUnwritableFiles();

        var (exit, output) = await Run("loadout", "files", "accept-disk", "-l", loadout.LoadoutId.ToString(), "-p", "{Game}/mod/a/one.txt");

        exit.Should().Be(0, output);
        Unwritable(loadout).Select(f => f.Path).Should().BeEquivalentTo([Gone]);
    }

    [Fact]
    public void ParsePath_ReadsWhatNameWrites()
    {
        var saved = new GamePath(LocationId.Saves, "slot/one.sav");

        UnwritableFile.ParsePath(UnwritableFile.Name(saved)).Should().Be(saved);
        UnwritableFile.ParsePath(UnwritableFile.Name(Kept)).Should().Be(Kept);
        UnwritableFile.Name(Kept).Should().Be("mod/a/one.txt", "a game folder path stays as it was listed");
    }

    [Fact]
    public void ParsePath_RefusesALocationWithNoPath()
    {
        var act = () => UnwritableFile.ParsePath("{Saves}");

        act.Should().Throw<UnwritableResolutionRefusedException>();
    }
}
