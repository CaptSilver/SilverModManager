using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests.Pages;

public class DiagnosticListViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    // The settings manager is shared with other UI tests, so put the default back. AUiTest is IAsyncDisposable,
    // which the runner calls instead of Dispose, so this is an override and not an IDisposable.
    public override async ValueTask DisposeAsync()
    {
        Provider.GetRequiredService<ISettingsManager>().Set(new DiagnosticSettings());
        await base.DisposeAsync();
    }

    private readonly record struct DetailsOnlyData(string Path) : IDiagnosticMessageData
    {
        public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, DiagnosticMessage message) => writer.Write(ref state, message.Value);
    }

    private static Diagnostic WithPath(string path) => new Diagnostic<DetailsOnlyData>
    {
        Id = new DiagnosticId(),
        Title = "missing file",
        Severity = DiagnosticSeverity.Warning,
        Summary = DiagnosticMessage.From("A file is missing"),
        Details = DiagnosticMessage.From("The file {Path} is missing"),
        DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        MessageData = new DetailsOnlyData(path),
    };

    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0xBEEF));

    private static Diagnostic Make(string title, DiagnosticSeverity severity) => new()
    {
        Id = new DiagnosticId(),
        Title = title,
        Severity = severity,
        Summary = DiagnosticMessage.From($"Summary of {title}"),
        Details = DiagnosticMessage.DefaultValue,
        DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
    };

    // One critical, two warnings, three suggestions. Built fresh each call, like a refresh does.
    private static Diagnostic[] Mixed() =>
    [
        Make("c1", DiagnosticSeverity.Critical),
        Make("w1", DiagnosticSeverity.Warning), Make("w2", DiagnosticSeverity.Warning),
        Make("s1", DiagnosticSeverity.Suggestion), Make("s2", DiagnosticSeverity.Suggestion), Make("s3", DiagnosticSeverity.Suggestion),
    ];

    private async Task<(DiagnosticListViewModel Vm, Subject<Diagnostic[]> Source)> Create()
    {
        var source = new Subject<Diagnostic[]>();
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(Arg.Any<LoadoutId>()).Returns(source);
        manager.RefreshAsync(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);

        var vm = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<DiagnosticListViewModel>(Provider, manager, new FakeWindowManager())));
        vm.LoadoutId = Loadout;
        await OnUi(() =>
        {
            vm.Activator.Activate();
            return Task.CompletedTask;
        });
        return (vm, source);
    }

    private void SetMinimumSeverity(DiagnosticSeverity severity) =>
        Provider.GetRequiredService<ISettingsManager>().Set(new DiagnosticSettings { MinimumSeverity = severity });

    [Fact]
    public async Task Test_TabCounts_FollowTheMinimumSeverity()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        source.OnNext(Mixed());
        await EventuallyOnUi(() => (vm.NumCritical, vm.NumWarnings, vm.NumSuggestions).Should().Be((1, 2, 3)));

        SetMinimumSeverity(DiagnosticSeverity.Warning);
        await EventuallyOnUi(() => (vm.NumCritical, vm.NumWarnings, vm.NumSuggestions).Should().Be((1, 2, 0)));

        SetMinimumSeverity(DiagnosticSeverity.Critical);
        await EventuallyOnUi(() => (vm.NumCritical, vm.NumWarnings, vm.NumSuggestions).Should().Be((1, 0, 0)));

        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        await EventuallyOnUi(() => (vm.NumCritical, vm.NumWarnings, vm.NumSuggestions).Should().Be((1, 2, 3)));
    }

    [Fact]
    public async Task Test_ARefreshWithTheSameDiagnostics_ReplacesTheEntriesWithEqualRows()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        source.OnNext(Mixed());
        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().HaveCount(6));
        var before = vm.DiagnosticEntries;

        source.OnNext(Mixed());
        await Task.Delay(500);

        await OnUi(() =>
        {
            vm.DiagnosticEntries.Should().NotBeSameAs(before, "the view keeps the scroll offset itself, so the rows are always rebuilt");
            vm.DiagnosticEntries.Select(e => e.Title).Should().Equal(before.Select(e => e.Title));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Test_ARefreshWithDifferentDiagnostics_ReplacesTheEntries()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        source.OnNext(Mixed());
        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().HaveCount(6));
        var before = vm.DiagnosticEntries;

        source.OnNext([.. Mixed(), Make("c2", DiagnosticSeverity.Critical)]);

        await EventuallyOnUi(() =>
        {
            vm.DiagnosticEntries.Should().HaveCount(7);
            vm.DiagnosticEntries.Should().NotBeSameAs(before);
        });
    }

    [Fact]
    public async Task Test_ARefreshThatChangesATitle_ReplacesTheEntries()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        var first = Mixed();
        source.OnNext(first);
        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().HaveCount(6));

        var changed = Mixed();
        changed[0] = changed[0] with { Id = first[0].Id, Title = "renamed" };
        source.OnNext(changed);

        await EventuallyOnUi(() => vm.DiagnosticEntries.Select(e => e.Title).Should().Contain("renamed"));
    }

    [Fact]
    public async Task Test_ARefreshThatChangesOnlyAFieldTheDetailsShow_ReplacesTheEntries()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        var first = WithPath("a.txt");
        source.OnNext([first]);
        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().HaveCount(1));
        var before = vm.DiagnosticEntries;

        source.OnNext([WithPath("b.txt") with { Id = first.Id }]);

        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().NotBeSameAs(before));
    }

    [Fact]
    public async Task Test_ARefreshWithEqualMessageData_ReplacesTheEntriesWithEqualRows()
    {
        SetMinimumSeverity(DiagnosticSeverity.Suggestion);
        var (vm, source) = await Create();
        var first = WithPath("a.txt");
        source.OnNext([first]);
        await EventuallyOnUi(() => vm.DiagnosticEntries.Should().HaveCount(1));
        var before = vm.DiagnosticEntries;

        source.OnNext([WithPath("a.txt") with { Id = first.Id }]);
        await Task.Delay(500);

        await OnUi(() =>
        {
            vm.DiagnosticEntries.Should().NotBeSameAs(before);
            vm.DiagnosticEntries.Select(e => e.Diagnostic.Id).Should().Equal(before.Select(e => e.Diagnostic.Id));
            return Task.CompletedTask;
        });
    }
}
