using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout diagnostic fix</c> and <c>loadout diagnostic clear</c> run what the Health Check page's buttons run: the
/// registered <see cref="IDiagnosticActionHandler"/> of a diagnostic's action, and the manager's clear actions.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutDiagnosticFixVerbTests(ITestOutputHelper helper) : EldenRingVerbTest<LoadoutDiagnosticFixVerbTests>(helper)
{
    private const string FixId = "test-fix";
    private const string DialogId = "test-asks-first";

    private readonly IDiagnosticManager _manager = Substitute.For<IDiagnosticManager>();
    private readonly List<(LoadoutId Loadout, ImmutableArray<string> Arguments)> _runs = [];
    private LoadoutItemGroupId? _modToSwitchOff;

    private static Diagnostic Make(string title, params DiagnosticAction[] actions) => new()
    {
        Id = new DiagnosticId("TEST", 7),
        Title = title,
        Severity = DiagnosticSeverity.Warning,
        Summary = DiagnosticMessage.From("summary of " + title),
        Details = DiagnosticMessage.From("details of " + title),
        DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        Actions = [..actions],
    };

    private static DiagnosticAction Fix(params string[] arguments) => new(FixId, "Fix it", [..arguments]);

    /// <summary>Stands in for an app handler: records the call and, like a real fix, writes to the loadout.</summary>
    private sealed class RecordingHandler(LoadoutDiagnosticFixVerbTests test, string id, bool unattended) : IDiagnosticActionHandler
    {
        public string ActionId => id;
        public bool RunsUnattended => unattended;

        public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
        {
            test._runs.Add((loadoutId, arguments));
            if (test._modToSwitchOff is not { } mod) return;
            using var tx = test.Connection.BeginTransaction();
            tx.Add(mod.Value, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services
            .AddSingleton(_manager)
            .AddSingleton<IDiagnosticActionHandler>(new RecordingHandler(this, FixId, unattended: true))
            .AddSingleton<IDiagnosticActionHandler>(new RecordingHandler(this, DialogId, unattended: false));
        return services;
    }

    private async Task<LoadoutId> LoadoutWith(params Diagnostic[] diagnostics)
    {
        var loadout = await CreateLoadout();
        _manager.GetLoadoutDiagnostics(loadout.LoadoutId).Returns(Observable.Return(diagnostics.ToArray()));
        _manager.RefreshAsync(loadout.LoadoutId).Returns(Task.CompletedTask);
        return loadout.LoadoutId;
    }

    [Fact]
    public async Task Fix_RunsTheHandlerOfTheActionWithItsArguments()
    {
        var loadout = await LoadoutWith(Make("Broken", Fix("a", "b")));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");

        exit.Should().Be(0, output);
        var run = _runs.Should().ContainSingle().Subject;
        run.Loadout.Should().Be(loadout);
        run.Arguments.Should().Equal("a", "b");
        output.Should().Contain("Fix it");
    }

    [Fact]
    public async Task Fix_FindsTheDiagnosticByItsId()
    {
        var loadout = await LoadoutWith(Make("Broken", Fix()));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "TEST: 7");

        exit.Should().Be(0, output);
        _runs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Fix_AnIdTwoDiagnosticsShare_IsRefusedWithTheirIndexes()
    {
        var loadout = await LoadoutWith(Make("First", Fix()), Make("Second", Fix()));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "TEST: 7");

        exit.Should().Be(1, output);
        output.Should().Contain("TEST: 7").And.Contain("0").And.Contain("1");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Fix_AnUnknownIndex_IsRefusedAndRunsNothing()
    {
        var loadout = await LoadoutWith(Make("Broken", Fix()));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "5");

        exit.Should().Be(1, output);
        output.Should().Contain("5").And.Contain("loadout diagnostics");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Fix_AnUnknownId_IsRefusedAndRunsNothing()
    {
        var loadout = await LoadoutWith(Make("Broken", Fix()));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "NOPE: 1");

        exit.Should().Be(1, output);
        output.Should().Contain("NOPE: 1");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Fix_ADiagnosticWithoutAnAction_IsRefusedNamingWhy()
    {
        var loadout = await LoadoutWith(Make("Just a note"));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");

        exit.Should().Be(1, output);
        output.Should().Contain("Just a note").And.Contain("no automatic fix");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Fix_AnActionNoHandlerRuns_IsRefused()
    {
        var loadout = await LoadoutWith(Make("Odd", new DiagnosticAction("nobody-handles-this", "Do it", [])));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");

        exit.Should().Be(1, output);
        // The words must hold whether or not the window is open: a missing handler is not about the window being closed.
        output.Should().Contain("nobody-handles-this").And.Contain("Health Check page").And.NotContain("is not open");
    }

    [Fact]
    public async Task Fix_AnActionThatAsksInADialog_IsRefusedInsteadOfHanging()
    {
        var loadout = await LoadoutWith(Make("Asks", new DiagnosticAction(DialogId, "Delete things", [])));

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");

        exit.Should().Be(1, output);
        output.Should().Contain("Delete things").And.Contain("Health Check page");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Fix_WithSeveralActions_NeedsOneChosenByIdAndRunsIt()
    {
        var loadout = await LoadoutWith(Make("Two ways", Fix("x"), new DiagnosticAction(DialogId, "Other", [])));

        var (refusedExit, refused) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");
        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0", "-a", FixId);

        refusedExit.Should().Be(1, refused);
        refused.Should().Contain(FixId).And.Contain(DialogId);
        exit.Should().Be(0, output);
        _runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Fix_ThenRevert_UndoesWhatTheFixChanged()
    {
        var loadout = await LoadoutWith(Make("Broken", Fix()));
        _modToSwitchOff = await AddMod(loadout, "Plain Mod");

        var (exit, output) = await Run("loadout", "diagnostic", "fix", "-l", loadout.ToString(), "-d", "0");
        exit.Should().Be(0, output);
        IsDisabled(_modToSwitchOff.Value).Should().BeTrue();

        var (revertExit, revertOutput) = await Run("loadout", "revert", "-l", loadout.ToString(), "-r", LatestRevisionNumber(loadout).ToString());

        revertExit.Should().Be(0, revertOutput);
        IsDisabled(_modToSwitchOff.Value).Should().BeFalse();
    }

    [Fact]
    public async Task List_PrintsTheIndexAndTheFixOfEachDiagnostic()
    {
        var loadout = await LoadoutWith(Make("Note"), Make("Broken", Fix()));

        var (exit, output) = await Run("loadout", "diagnostics", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("[1] Warning: Broken").And.Contain($"loadout diagnostic fix -l {loadout} -d 1");
    }

    [Fact]
    public async Task List_SaysWhereAFixThatCannotRunHereCanRun()
    {
        var loadout = await LoadoutWith(Make("Asks", new DiagnosticAction(DialogId, "Delete things", [])),
            Make("Odd", new DiagnosticAction("nobody-handles-this", "Do it", [])));

        var (exit, output) = await Run("loadout", "diagnostics", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("Fix: Delete things (needs the Health Check page)");
        output.Should().Contain("Fix: Do it (no command line fix, use the Health Check page)");
    }

    [Fact]
    public async Task List_Json_CarriesTheIndexAndTheFixes()
    {
        var loadout = await LoadoutWith(Make("Broken", new DiagnosticAction(FixId, "Fix it", [], "Takes a while")));

        var (exit, output) = await Run("loadout", "diagnostics", "-l", loadout.ToString(), "--json");

        exit.Should().Be(0, output);
        var row = JsonNode.Parse(output)!.AsArray().Single()!;
        ((int)row["index"]!).Should().Be(0);
        var fix = row["fixes"]!.AsArray().Single()!;
        ((string)fix["id"]!).Should().Be(FixId);
        ((string)fix["label"]!).Should().Be("Fix it");
        ((string)fix["note"]!).Should().Be("Takes a while");
        ((bool)fix["canRunHere"]!).Should().BeTrue();
    }

    [Fact]
    public async Task Clear_RunsEveryClearActionAndRefreshes()
    {
        var loadout = await LoadoutWith();
        var ran = new List<string>();
        _manager.GetClearActions(loadout).Returns([
            new DiagnosticClearAction("Clear crash logs", _ => { ran.Add("crash"); return Task.FromResult("Cleared 3 crash logs."); }),
            new DiagnosticClearAction("Clear dumps", _ => { ran.Add("dumps"); return Task.FromResult("Cleared 1 dump."); }),
        ]);

        var (exit, output) = await Run("loadout", "diagnostic", "clear", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        ran.Should().Equal("crash", "dumps");
        output.Should().Contain("Cleared 3 crash logs.").And.Contain("Cleared 1 dump.");
        await _manager.Received(1).RefreshAsync(loadout);
    }

    [Fact]
    public async Task Clear_WithNothingToClear_SaysSo()
    {
        var loadout = await LoadoutWith();
        _manager.GetClearActions(loadout).Returns([]);

        var (exit, output) = await Run("loadout", "diagnostic", "clear", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("Nothing to clear");
        await _manager.DidNotReceive().RefreshAsync(loadout);
    }

    [Fact]
    public async Task Clear_AFailingAction_IsReportedAndTheOthersStillRun()
    {
        var loadout = await LoadoutWith();
        _manager.GetClearActions(loadout).Returns([
            new DiagnosticClearAction("Clear crash logs", _ => throw new IOException("disk is read-only")),
            new DiagnosticClearAction("Clear dumps", _ => Task.FromResult("Cleared 1 dump.")),
        ]);

        var (exit, output) = await Run("loadout", "diagnostic", "clear", "-l", loadout.ToString());

        exit.Should().Be(1, output);
        output.Should().Contain("Clear crash logs").And.Contain("disk is read-only").And.Contain("Cleared 1 dump.");
    }
}
