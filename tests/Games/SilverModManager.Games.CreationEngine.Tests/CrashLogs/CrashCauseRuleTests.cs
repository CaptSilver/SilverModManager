using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.CrashLogs;
using SilverModManager.Games.CreationEngine.CrashLogs.Rules;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

public class CrashCauseRuleTests(IServiceProvider serviceProvider)
{
    private static readonly GamePath Mesh = CrashLogPaths.Classify(@"Plants\GlowingMushroomCluster01.nif")!.Value;

    private ICreationEngineGame Vr => serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimVR.SkyrimVR>().Single();

    private ICreationEngineGame Se => serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single();

    private static LoadoutItemGroupReference Ref(ulong n) =>
        new() { TxId = TxId.From(1), DataId = LoadoutItemGroupId.From(EntityId.From(n)) };

    private static CrashOwnership MushroomOwner() =>
        new([new OwnedPath(Mesh, new ModOwner(Ref(42), "Glowing Mushrooms"), false)], [], [], []);

    private static CrashReport WithObject(ObjectDetails details) => new() { ObjectDetails = [details] };

    private static ObjectDetails Object(string? parenthetical, DetailLine[] own, DetailLine[] parents) =>
        new("RSP+10", "BSTriShape", "X", parenthetical, own, parents);

    [Fact]
    public void EnbLightOnAVrGameNamesTheMeshOwnerAsTheFix()
    {
        var verdict = CrashCauseRules.FirstMatch(Fixtures.Parse("enb-light-mushroom-a.log"), MushroomOwner(), Vr);

        verdict.Should().NotBeNull();
        verdict!.NamedMod.Should().Be(Ref(42));
        verdict.SubjectFile.Should().Be(Mesh);
        verdict.Startup.Should().BeFalse();
        verdict.Cause.Should().Be("A mesh with ENB particle lights was drawn; ENB Light does not work in Skyrim VR");
        verdict.Fix.Should().Be("Disable the mod that supplies the mesh, or replace the mesh with a version without ENB Light");
    }

    [Fact]
    public void EnbLightEvidenceInAParentBlockNamesTheChildsMesh()
    {
        var report = WithObject(Object(@"Plants\Foo.nif", [new("RTTIName", "BSTriShape")], [new("Name", "ENB Light 07")]));

        var verdict = CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr);

        verdict.Should().NotBeNull();
        verdict!.SubjectFile.Should().Be(CrashLogPaths.Classify(@"Plants\Foo.nif"));
        verdict.NamedMod.Should().BeNull("no enabled mod supplies Foo.nif in this ownership");
    }

    [Fact]
    public void TheMeshComesFromAFileLineWhenTheParentheticalIsNotAMesh()
    {
        var report = WithObject(Object("Skyrim.esm", [new("File", @"Plants\Bar.nif"), new("Name", "ENB Light 01")], []));

        CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr)!.SubjectFile
            .Should().Be(CrashLogPaths.Classify(@"Plants\Bar.nif"));
    }

    [Fact]
    public void ABillboardNodeAloneIsNotEnbLight()
    {
        var report = WithObject(Object(@"Plants\Foo.nif", [new("RTTIName", "BSTriShape")], [new("RTTIName", "NiBillboardNode")]));

        CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr).Should().BeNull();
    }

    [Fact]
    public void AnEnbTextureIsEvidenceToo()
    {
        var report = WithObject(Object(@"Plants\Foo.nif", [new("Texture[Diffuse]", @"textures\effects\enb\x.dds")], []));

        CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr).Should().NotBeNull();
    }

    [Fact]
    public void ATextureOutsideTheEnbFolderIsNotEvidence()
    {
        var report = WithObject(Object(@"Plants\Foo.nif", [new("Texture[Diffuse]", @"textures\effects\fire.dds")], []));

        CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr).Should().BeNull();
    }

    [Fact]
    public void EnbLightOnSkyrimSeIsALeadWithoutAFix()
    {
        var verdict = CrashCauseRules.FirstMatch(Fixtures.Parse("enb-light-mushroom-a.log"), MushroomOwner(), Se);

        verdict.Should().NotBeNull();
        verdict!.NamedMod.Should().BeNull();
        verdict.SubjectFile.Should().Be(Mesh);
        verdict.Fix.Should().Be("No known fix");
        verdict.Cause.Should().Contain("is a lead: ENB Light works on Skyrim SE, so look at the mesh's other changes");
    }

    [Fact]
    public void RendererInitUnderThirtySecondsPointsAtLaunchOptions()
    {
        var verdict = CrashCauseRules.FirstMatch(Fixtures.Parse("renderer-init.log"), CrashOwnership.Empty, Vr);

        verdict.Should().NotBeNull();
        verdict!.Startup.Should().BeTrue();
        verdict.Cause.Should().Be("The renderer failed to start");
        verdict.Fix.Should().Contain(WineParser.WineDllOverridesEnvironmentVariableName).And.Contain("launch options");
    }

    [Fact]
    public void RendererInitAfterAMinuteDoesNotMatch()
    {
        var report = Fixtures.Parse("renderer-init.log") with { Uptime = TimeSpan.FromSeconds(61) };

        CrashCauseRules.FirstMatch(report, CrashOwnership.Empty, Vr).Should().BeNull();
    }

    [Fact]
    public void MissingMastersWithNoPluginsLoadedIsAStartupCrash()
    {
        var verdict = CrashCauseRules.FirstMatch(Fixtures.Parse("missing-masters.log"), CrashOwnership.Empty, Vr);

        verdict.Should().NotBeNull();
        verdict!.Startup.Should().BeTrue();
        verdict.Cause.Should().Be("The game stopped loading plugins because a master is missing");
        verdict.Fix.Should().Contain("Missing Master");
    }

    [Theory]
    [InlineData("light-list-bread.log")]
    [InlineData("script-vm.log")]
    [InlineData("water-ripples.log")]
    public void AnUnmatchedReportHasNoVerdict(string fixture)
    {
        CrashCauseRules.FirstMatch(Fixtures.Parse(fixture), CrashOwnership.Empty, Vr).Should().BeNull();
    }

    [Fact]
    public void OwnerOfSearchesAssetsPluginsAndDlls()
    {
        var plugin = CrashLogPaths.Classify("Foo.esp")!.Value;
        var dll = CrashLogPaths.Classify("Bar.dll")!.Value;
        var owner = new ModOwner(Ref(7), "Foo");
        var owners = new CrashOwnership([], [new OwnedPath(plugin, owner, false)], [new OwnedPath(dll, null, false)], []);

        owners.OwnerOf(plugin).Should().Be(owner);
        owners.OwnerOf(dll).Should().BeNull("the DLL has no owner");
        owners.OwnerOf(Mesh).Should().BeNull();
    }
}
