using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// Which failures while planning the merges are a problem with the mods' files (a refusal the player can act on) and
/// which are bugs that have to reach the crash report instead of being worded as "can't merge".
/// </summary>
public class MergePlanFailureTests(ITestOutputHelper helper) : AVanillaDumpTest<MergePlanFailureTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private Exception? _failIndexWith;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<LoadoutMergeSources>(provider => new FailingSources(
            this,
            provider.GetRequiredService<IFileStore>(),
            provider.GetRequiredService<RegulationBaselines>(),
            provider.GetRequiredService<VanillaDumps>(),
            provider.GetRequiredService<ArchiveVanillas>(),
            provider.GetRequiredService<InstalledBuilds>(),
            provider.GetRequiredService<ILogger<LoadoutMergeSources>>()));
        return services;
    }

    private sealed class FailingSources(MergePlanFailureTests test, IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, ArchiveVanillas archives, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
        : LoadoutMergeSources(fileStore, baselines, dumps, archives, builds, logger)
    {
        public override MergePathIndex Index(Loadout.ReadOnly loadout)
            => test._failIndexWith is { } failure ? throw failure : base.Index(loadout);
    }

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text },
        }, Formats);

    private async Task<Loadout.ReadOnly> TwoMsgbndMods()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text")));
        return Refresh(loadout);
    }

    // The apply lists the mods' files before it plans, and a listing failure is worded differently, so the listing is
    // answered from what it remembered before the planner is made to fail.
    private void WarmTheListing(Loadout.ReadOnly loadout) => ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout);

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<MergeDiagnosticsEmitter>().Single();
        var diagnostics = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(Refresh(loadout), CancellationToken.None)) diagnostics.Add(diagnostic);
        return diagnostics.ToArray();
    }

    [Fact]
    public async Task AMergeFailureWhilePlanningRefusesTheApplyAsPlanFailed()
    {
        var loadout = await TwoMsgbndMods();
        WarmTheListing(loadout);
        _failIndexWith = new MergeException("cannot join");

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var thrown = (await act.Should().ThrowAsync<MergeRefusedException>()).Which;
        thrown.Message.Should().Be(MergeRefusedException.PlanFailed(new MergeException("cannot join")).Message);
    }

    [Fact]
    public async Task ABugWhilePlanningReachesTheApplyUnwrapped()
    {
        var loadout = await TwoMsgbndMods();
        WarmTheListing(loadout);
        _failIndexWith = new InvalidOperationException("a planner bug");

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("a planner bug");
    }

    [Fact]
    public async Task AMergeFailureWhilePlanningIsACriticalDiagnostic()
    {
        var loadout = await TwoMsgbndMods();
        _failIndexWith = new MergeException("cannot join");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 12));
    }

    [Fact]
    public async Task ABugWhilePlanningReachesTheDiagnosticsRunUnwrapped()
    {
        var loadout = await TwoMsgbndMods();
        _failIndexWith = new InvalidOperationException("a planner bug");

        var act = async () => await Diagnose(loadout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("a planner bug");
    }

    public static TheoryData<Exception, bool> Failures => new()
    {
        { new MergeException("m"), true },
        { new FmgMergeException("m"), true },
        { new EsdMergeException("m"), true },
        { new ParamException("m"), true },
        { new TpfException("m"), true },
        { new EsdException("m"), true },
        { new FmgException("m"), true },
        { new DcxException("m"), true },
        { new RegulationException("m"), true },
        { new Bnd3Exception("m"), true },
        { new Bnd4Exception("m"), true },
        { new Bhd5Exception("m"), true },
        { new AesException("m"), true },
        { new KrakenException("m"), true },
        { new InvalidOperationException("m"), false },
        { new IndexOutOfRangeException("m"), false },
        { new NullReferenceException("m"), false },
        { new IOException("m"), false },
        { new TypeInitializationException("Ooz", null), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void OnlyTheMergeAndFormatFamilyIsAProblemWithTheInput(Exception exception, bool expected)
        => MergeFailures.IsInputProblem(exception).Should().Be(expected);
}
