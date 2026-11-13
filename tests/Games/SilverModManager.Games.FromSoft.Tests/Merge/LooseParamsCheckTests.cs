using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class LooseParamsCheckTests
{
    private const string LooseParam = "param/gameparam/gameparam.parambnd.dcx";

    private static readonly LooseParamsRule Rule = new([LooseParam, "param/gameparam/gameparam_dlc1.parambnd.dcx"], "Data0.bdt");

    private static ProviderFile Provider(string package, string relPath)
        => new(package, relPath, FileIdentity.XxHash3(Hash.From((ulong)(package + relPath).GetHashCode())), 1, package);

    [Fact]
    public void BothKindsPresentNameBothMods()
    {
        var verdict = LooseParamsCheck.Check(Rule, [Provider("ModA", LooseParam), Provider("ModB", "Data0.bdt")]);

        verdict!.LooseParamMods.Should().Equal("ModA");
        verdict.RegulationMods.Should().Equal("ModB");
    }

    [Fact]
    public void LooseParamsAloneAreAVerdictWithoutRegulationMods()
    {
        var verdict = LooseParamsCheck.Check(Rule, [Provider("ModA", "param/gameparam/gameparam_dlc1.parambnd.dcx")]);

        verdict!.LooseParamMods.Should().Equal("ModA");
        verdict.RegulationMods.Should().BeEmpty();
    }

    [Fact]
    public void TheRegulationAloneIsNoVerdict()
    {
        LooseParamsCheck.Check(Rule, [Provider("ModB", "Data0.bdt")]).Should().BeNull();
    }

    [Fact]
    public void AGameWithoutTheRuleGetsNoVerdict()
    {
        LooseParamsCheck.Check(null, [Provider("ModA", LooseParam), Provider("ModB", "Data0.bdt")]).Should().BeNull();
    }

    [Fact]
    public void AFileWithTheSameNameInAnotherFolderDoesNotCount()
    {
        LooseParamsCheck.Check(Rule, [Provider("ModA", "mods/" + LooseParam), Provider("ModB", "Data0.bdt")]).Should().BeNull();
    }

    [Fact]
    public void TheNamesAreMatchedWithoutRegardToCase()
    {
        var verdict = LooseParamsCheck.Check(Rule, [Provider("ModA", "Param/GameParam/GameParam.parambnd.dcx"), Provider("ModB", "data0.BDT")]);

        verdict!.LooseParamMods.Should().Equal("ModA");
        verdict.RegulationMods.Should().Equal("ModB");
    }

    [Fact]
    public void ModsAreListedOnceEvenWithSeveralFiles()
    {
        var verdict = LooseParamsCheck.Check(Rule, [Provider("ModA", LooseParam), Provider("ModA", "param/gameparam/gameparam_dlc1.parambnd.dcx")]);

        verdict!.LooseParamMods.Should().Equal("ModA");
    }
}

public class LooseParamsApplyTests(ITestOutputHelper helper) : AModdedTestSoulsTest<LooseParamsApplyTests>(helper)
{
    private static GamePath InMods(string path) => new(LocationId.Me3Mods, path);

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<LooseParamsEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) found.Add(diagnostic);
        return found.ToArray();
    }

    [Fact]
    public async Task ApplyingALoadoutWithLooseParamsAndTheRegulationIsRefusedNamingBothMods()
    {
        var loadout = await CreateLoadout();
        await AddModWithFiles(loadout, "Param Mod", InMods("Param Mod/" + "param/gameparam/gameparam.parambnd.dcx"));
        await AddModWithFiles(loadout, "Regulation Mod", InMods("Regulation Mod/Data0.bdt"));

        var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var refused = (await apply.Should().ThrowAsync<LooseParamsRefusedException>()).Which;
        refused.Should().BeAssignableTo<UserFacingException>();
        refused.Message.Should().Contain("Param Mod").And.Contain("Regulation Mod").And.Contain("Data0.bdt");
    }

    [Fact]
    public async Task ApplyingLooseParamsAloneIsNotRefused()
    {
        var loadout = await CreateLoadout();
        await AddModWithFiles(loadout, "Param Mod", InMods("Param Mod/param/gameparam/gameparam.parambnd.dcx"));

        var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

        await apply.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LooseParamsAloneYieldOneInformationDiagnostic()
    {
        var loadout = await CreateLoadout();
        await AddModWithFiles(loadout, "Param Mod", InMods("Param Mod/param/gameparam/gameparam.parambnd.dcx"));

        var diagnostic = (await Diagnose(Refresh(loadout))).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Data0.bdt").And.Contain("loose params");
    }

    [Fact]
    public async Task NoLooseParamsNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await AddModWithFiles(loadout, "Regulation Mod", InMods("Regulation Mod/Data0.bdt"));

        (await Diagnose(Refresh(loadout))).Should().BeEmpty();
    }
}
