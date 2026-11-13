using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermodmanager fromsoft merge choices</c> over a loadout whose two mods edit the same message text, so the
/// path holds exactly one contested entry, <c>fmg:1/10</c>.
/// </summary>
[Collection("ConsoleOutput")]
public class FromSoftMergeChoiceVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<FromSoftMergeChoiceVerbTests, EldenRingGame>(helper)
{
    private const string StubbedSteamId = "3976631895";
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string Key = "fmg:1/10";

    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(null, TestScratch.Combine($"MergeChoiceVerbs.{Guid.NewGuid():N}"));
        base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From(StubbedSteamId)],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        return services;
    }

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text },
        }, Formats);

    private async Task InstallPackage(Loadout.ReadOnly loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        // Table cells are part of what a user reads, so they join the text lines.
        var table = renderer.Logs.OfType<Table>().Any() ? string.Join('\n', renderer.LastCellsAsStrings().Select(row => string.Join(" | ", row))) + "\n" : "";
        return (exitCode, table + string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    private async Task<Loadout.ReadOnly> MergingPair()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout, "Beta", (Msg, Msgbnd("beta text")));
        return Refresh(loadout);
    }

    private async Task<JsonObject[]> ListRows(Loadout.ReadOnly loadout, params string[] extra)
    {
        var (exit, output) = await Run(["fromsoft", "merge", "choices", "list", "-l", loadout.Name, "--json", .. extra]);
        exit.Should().Be(0, output);
        return JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
    }

    private async Task<MergeContest> Contest(Loadout.ReadOnly loadout)
    {
        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);
        return report.Entries.Single().Contests.Single();
    }

    private int StoredChoices(Loadout.ReadOnly loadout) => LoadoutMergeChoice.FindByLoadout(Connection.Db, loadout.LoadoutId).Count();

    [Fact]
    public async Task List_ShowsContestsWithTheWinnerAsAField()
    {
        var loadout = await MergingPair();

        var (exit, output) = await Run("fromsoft", "merge", "choices", "list", "-l", loadout.Name);

        exit.Should().Be(0, output);
        var contest = await Contest(loadout);
        contest.Candidates.Should().HaveCount(2);
        output.Should().Contain(Msg).And.Contain(Key).And.Contain(contest.Winner).And.Contain("priority");
        output.TrimEnd().Split('\n').Last().Should().Be("A choice is applied on the next Apply; the winner is the mod whose edit the merged file keeps");
    }

    [Fact]
    public async Task Json_RowsCarryKeyWinnerChosen()
    {
        var loadout = await MergingPair();
        var contest = await Contest(loadout);

        var row = (await ListRows(loadout)).Should().ContainSingle().Which;

        row["path"]!.GetValue<string>().Should().Be(Msg);
        row["key"]!.GetValue<string>().Should().Be(Key);
        row["candidates"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal(contest.Candidates);
        row["winner"]!.GetValue<string>().Should().Be(contest.Winner);
        row["chosen"]!.GetValue<bool>().Should().BeFalse();
        row["choiceId"].Should().BeNull();
    }

    [Fact]
    public async Task Set_PinsTheWinner_AndTheReportSaysChosen()
    {
        var loadout = await MergingPair();
        var loser = (await Contest(loadout)).Candidates[0];

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", loser);

        exit.Should().Be(0, output);
        output.Should().Contain($"{loser} now wins {Key} of {Msg}").And.Contain("apply the loadout");
        var row = (await ListRows(loadout)).Should().ContainSingle().Which;
        row["winner"]!.GetValue<string>().Should().Be(loser);
        row["chosen"]!.GetValue<bool>().Should().BeTrue();
        row["choiceId"].Should().NotBeNull();
        var (_, text) = await Run("fromsoft", "merge", "choices", "list", "-l", loadout.Name);
        text.Should().Contain("choice");
        var (_, report) = await Run("fromsoft", "merge", "list", "-l", loadout.Name, "--json");
        JsonDocument.Parse(report).RootElement.GetProperty("entries")[0].GetProperty("contests")[0].GetProperty("chosen").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Set_ReplacesAnEarlierChoiceOfTheSameEntry()
    {
        var loadout = await MergingPair();
        var candidates = (await Contest(loadout)).Candidates;
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", candidates[0]);

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", candidates[1]);

        exit.Should().Be(0, output);
        StoredChoices(loadout).Should().Be(1);
        (await ListRows(loadout)).Single()["winner"]!.GetValue<string>().Should().Be(candidates[1]);
    }

    [Fact]
    public async Task Set_UnknownCandidate_IsRefused()
    {
        var loadout = await MergingPair();
        await InstallPackage(loadout, "Gamma", ("regulation.bin", [1, 2, 3]));
        loadout = Refresh(loadout);
        var candidates = (await Contest(loadout)).Candidates;

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", "Gamma.zip");

        exit.Should().Be(1, output);
        output.Should().Contain(candidates[0]).And.Contain(candidates[1]);
        StoredChoices(loadout).Should().Be(0);
    }

    [Fact]
    public async Task Set_NoContest_IsRefused()
    {
        var loadout = await MergingPair();
        var winner = (await Contest(loadout)).Winner;

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", "fmg:1/99", "--winner", winner);

        exit.Should().Be(1, output);
        output.Should().Contain("Nothing contests fmg:1/99").And.Contain("fromsoft merge choices list");
        StoredChoices(loadout).Should().Be(0);
    }

    [Fact]
    public async Task Set_BinderMember_IsRefused()
    {
        var loadout = await MergingPair();
        var winner = (await Contest(loadout)).Winner;

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", "member:foo", "--winner", winner);

        exit.Should().Be(1, output);
        output.Should().Contain("Binder members cannot be chosen yet");
        StoredChoices(loadout).Should().Be(0);
    }

    [Fact]
    public async Task Set_AKeyThatIsNoKindOfEntry_IsRefused()
    {
        var loadout = await MergingPair();
        var winner = (await Contest(loadout)).Winner;

        var (exit, output) = await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", "whatever", "--winner", winner);

        exit.Should().Be(1, output);
        output.Should().Contain("whatever is not a choosable entry");
    }

    [Fact]
    public async Task Set_ThenRevert_RestoresPriorityWinner()
    {
        var loadout = await MergingPair();
        var before = await Contest(loadout);
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", before.Candidates[0]);
        var revision = ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.LoadoutId.Value).Count - 1;

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", revision.ToString());

        exit.Should().Be(0, output);
        StoredChoices(loadout).Should().Be(0);
        var row = (await ListRows(loadout)).Should().ContainSingle().Which;
        row["chosen"]!.GetValue<bool>().Should().BeFalse();
        row["winner"]!.GetValue<string>().Should().Be(before.Winner);
    }

    [Fact]
    public async Task Clear_RemovesTheChoice()
    {
        var loadout = await MergingPair();
        var contest = await Contest(loadout);
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", contest.Candidates[0]);

        var (exit, output) = await Run("fromsoft", "merge", "choices", "clear", "-l", loadout.Name, "--path", Msg, "--entry", Key);

        exit.Should().Be(0, output);
        StoredChoices(loadout).Should().Be(0);
        (await ListRows(loadout)).Single()["chosen"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ClearAll_RemovesEveryChoiceOfTheLoadout()
    {
        var loadout = await MergingPair();
        var contest = await Contest(loadout);
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", contest.Candidates[0]);

        var (exit, output) = await Run("fromsoft", "merge", "choices", "clear", "-l", loadout.Name, "--all");

        exit.Should().Be(0, output);
        StoredChoices(loadout).Should().Be(0);
    }

    [Fact]
    public async Task Clear_WithNeitherAnEntryNorAll_IsRefused()
    {
        var loadout = await MergingPair();
        var contest = await Contest(loadout);
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", contest.Candidates[0]);

        var (exit, output) = await Run("fromsoft", "merge", "choices", "clear", "-l", loadout.Name);

        exit.Should().Be(1, output);
        output.Should().Contain("--all");
        StoredChoices(loadout).Should().Be(1);
    }

    [Fact]
    public async Task Clear_AnEntryWithoutAPath_IsRefused()
    {
        var loadout = await MergingPair();
        var contest = await Contest(loadout);
        await Run("fromsoft", "merge", "choices", "set", "-l", loadout.Name, "--path", Msg, "--entry", Key, "--winner", contest.Candidates[0]);

        var (exit, output) = await Run("fromsoft", "merge", "choices", "clear", "-l", loadout.Name, "--entry", Key);

        exit.Should().Be(1, output);
        output.Should().Contain("--entry needs --path");
        StoredChoices(loadout).Should().Be(1);
    }

    [Fact]
    public async Task List_AChoiceForAModThatDoesNotEditTheEntry_IsListedAsStaleWithThePriorityWinner()
    {
        var loadout = await MergingPair();
        await InstallPackage(loadout, "Gamma", ("regulation.bin", [1, 2, 3]));
        loadout = Refresh(loadout);
        var contest = await Contest(loadout);
        var gamma = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Gamma.zip");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutMergeChoice.New(tx) { LoadoutId = loadout.LoadoutId, Path = Msg, Key = Key, WinnerGroupId = LoadoutItemGroupId.From(gamma.Id) };
            await tx.Commit();
        }

        var row = (await ListRows(loadout)).Should().ContainSingle().Which;
        var (_, text) = await Run("fromsoft", "merge", "choices", "list", "-l", loadout.Name);

        row["chosen"]!.GetValue<bool>().Should().BeFalse();
        row["winner"]!.GetValue<string>().Should().Be(contest.Winner);
        row["choiceId"].Should().NotBeNull();
        text.Should().Contain("stale (Gamma.zip no longer edits it)");
    }

    [Fact]
    public async Task List_AChoiceNothingContestsAnyMore_IsListedAsStale()
    {
        var loadout = await MergingPair();
        var group = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().First();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutMergeChoice.New(tx) { LoadoutId = loadout.LoadoutId, Path = Msg, Key = "fmg:1/99", WinnerGroupId = LoadoutItemGroupId.From(group.Id) };
            await tx.Commit();
        }

        var rows = await ListRows(loadout);
        var (_, text) = await Run("fromsoft", "merge", "choices", "list", "-l", loadout.Name);

        rows.Should().HaveCount(2);
        rows.Single(r => r["key"]!.GetValue<string>() == "fmg:1/99")["chosen"]!.GetValue<bool>().Should().BeFalse();
        text.Should().Contain("stale (nothing contests it)");
    }
}
