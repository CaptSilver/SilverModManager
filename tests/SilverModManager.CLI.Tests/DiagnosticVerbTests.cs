using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.CommandLine.Verbs;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod loadout diagnostics</c> against a loadout in the test database and a diagnostic manager that returns
/// made-up diagnostics.
/// </summary>
public class DiagnosticVerbTests(IServiceProvider provider)
{
    /// <summary>Message data whose only field is a type no formatter knows.</summary>
    private readonly struct UnformattedData : IDiagnosticMessageData
    {
        private sealed class Opaque
        {
            public override string ToString() => "opaque-value";
        }

        public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, DiagnosticMessage message)
        {
            writer.Write(ref state, "Saw ");
            writer.Write(ref state, new Opaque());
        }
    }

    private static Diagnostic Plain(DiagnosticSeverity severity, string title, string summary, string details) => new()
    {
        Id = new DiagnosticId("TEST", 7),
        Title = title,
        Severity = severity,
        Summary = DiagnosticMessage.From(summary),
        Details = DiagnosticMessage.From(details),
        DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
    };

    private async Task<LoadoutId> SeedLoadout()
    {
        var connection = provider.GetRequiredService<IConnection>();
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/diagnostic-verb-tests-" + Guid.NewGuid(),
            Name = "Diagnostic Verb Game",
            Store = GameStore.Unknown,
            Game = GameId.From("DiagnosticVerbGame-" + Guid.NewGuid()),
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Diagnostic Verb Loadout " + Guid.NewGuid(),
            ShortName = "DV" + Guid.NewGuid().ToString("N")[..6],
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout).LoadoutId;
    }

    private async Task<(int ExitCode, string Output, IDiagnosticManager Manager)> Run(Diagnostic[] diagnostics, params string[] extraArgs)
    {
        var loadoutId = await SeedLoadout();
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(loadoutId).Returns(Observable.Return(diagnostics));
        manager.RefreshAsync(loadoutId).Returns(Task.CompletedTask);

        var services = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton(provider.GetRequiredService<IConnection>())
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddModule("loadout", "Commands for managing a specific loadout")
            .AddSingleton(manager)
            .AddSingleton<IDiagnosticWriter, PlainDiagnosticWriter>()
            .AddDiagnosticVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var args = new[] { "loadout", "diagnostics", "--loadout", loadoutId.Value.ToString() }.Concat(extraArgs).ToArray();
        var exitCode = await services.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)), manager);
    }

    [Fact]
    public async Task PrintsSeverityTitleSummaryAndDetailsOfEachDiagnostic()
    {
        var (exitCode, output, _) = await Run([Plain(DiagnosticSeverity.Warning, "Missing file", "Two files are gone", "Reinstall **mod A**.")]);

        exitCode.Should().Be(0);
        output.Should().Contain("Warning").And.Contain("Missing file").And.Contain("Two files are gone").And.Contain("Reinstall **mod A**.");
    }

    [Fact]
    public async Task NoDiagnosticsSaysSoInsteadOfPrintingNothing()
    {
        var (exitCode, output, _) = await Run([]);

        exitCode.Should().Be(0);
        output.Trim().Should().Be("No problems were found.");
    }

    [Fact]
    public async Task RefreshRunsTheEmittersAgainBeforeReading()
    {
        var (exitCode, _, manager) = await Run([], "--refresh");

        exitCode.Should().Be(0);
        await manager.Received(1).RefreshAsync(Arg.Any<LoadoutId>());
    }

    [Fact]
    public async Task WithoutRefreshTheEmittersAreNotRerun()
    {
        var (_, _, manager) = await Run([]);

        await manager.DidNotReceive().RefreshAsync(Arg.Any<LoadoutId>());
    }

    [Fact]
    public async Task JsonHasIdSeverityTitleSummaryAndDetails()
    {
        var (exitCode, output, _) = await Run([Plain(DiagnosticSeverity.Critical, "Broken", "It broke", "Fix it.")], "--json");

        exitCode.Should().Be(0);
        using var doc = JsonDocument.Parse(output);
        var row = doc.RootElement.EnumerateArray().Single();
        row.GetProperty("id").GetString().Should().Be("TEST: 7");
        row.GetProperty("severity").GetString().Should().Be("Critical");
        row.GetProperty("title").GetString().Should().Be("Broken");
        row.GetProperty("summary").GetString().Should().Be("It broke");
        row.GetProperty("details").GetString().Should().Be("Fix it.");
    }

    [Fact]
    public async Task NoDiagnosticsIsAnEmptyJsonArray()
    {
        var (_, output, _) = await Run([], "--json");

        JsonDocument.Parse(output).RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AValueWithoutAFormatterPrintsItsToStringInsteadOfThrowing()
    {
        var diagnostic = new Diagnostic<UnformattedData>
        {
            Id = new DiagnosticId("TEST", 8),
            Title = "Opaque",
            Severity = DiagnosticSeverity.Suggestion,
            Summary = DiagnosticMessage.From("unused"),
            Details = DiagnosticMessage.From("unused"),
            DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
            MessageData = default,
        };

        var (exitCode, output, _) = await Run([diagnostic]);

        exitCode.Should().Be(0);
        output.Should().Contain("Saw opaque-value");
    }

    [Fact]
    public void PlainWriterWritesTextAndValuesAsToString()
    {
        var writer = new PlainDiagnosticWriter();
        var state = new DiagnosticWriterState(DiagnosticWriterMode.PlainText);

        writer.Write(ref state, "count: ");
        writer.WriteValueType(ref state, 42);
        writer.Write(ref state, " of ");
        writer.Write(ref state, new Uri("http://example.test/"));

        state.ToOutput().Should().Be("count: 42 of http://example.test/");
    }
}
