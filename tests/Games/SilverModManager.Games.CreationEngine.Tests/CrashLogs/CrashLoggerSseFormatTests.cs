using FluentAssertions;
using SilverModManager.Games.CreationEngine.CrashLogs;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

public class CrashLoggerSseFormatTests
{
    private const string EnbA = "enb-light-mushroom-a.log";

    [Fact]
    public void ReadsTheHeaderExceptionModuleOffsetAndInstruction()
    {
        var report = Fixtures.Parse(EnbA);

        report.CrashTime.Should().Be("2026-11-09 15:30:26");
        report.GameVersion.Should().Be("Skyrim VR v1.4.15");
        report.LoggerVersion.Should().Be("CrashLoggerSSE v1-27-0-0");
        report.ExceptionCode.Should().Be("EXCEPTION_ACCESS_VIOLATION");
        report.FaultModule.Should().Be("d3d11.dll");
        report.FaultOffset.Should().Be("00FCE60");
        report.FaultInstruction.Should().Be("lock inc [rsi]");
        report.ThreadRole.Should().Be("Rendering");

        var renderer = Fixtures.Parse("renderer-init.log");
        renderer.FaultModule.Should().Be("SkyrimVR.exe");
        renderer.FaultOffset.Should().Be("0DC492C");
    }

    [Fact]
    public void ReadsAFileWithMixedLineEndings()
    {
        var text = Fixtures.Text(EnbA);
        text.Should().Contain("\r\n");
        System.Text.RegularExpressions.Regex.IsMatch(text, "(?<!\r)\n").Should().BeTrue("the STACK block is written with bare LF");

        var report = Fixtures.Parse(EnbA);
        var strings = report.Frames.Select(f => f.Symbol ?? "")
            .Concat(report.ObjectDetails.SelectMany(o => o.All).SelectMany(l => new[] { l.Key, l.Value }))
            .Concat(report.Plugins);
        strings.Should().NotContain(s => s.Contains('\r'));
    }

    [Fact]
    public void ReadsASectionHeaderWithAParenthesisedSuffix()
    {
        Fixtures.Parse(EnbA).Frames.Should().NotBeEmpty();
        Fixtures.Parse("water-ripples.log").ResourcePaths.Should().Equal(@"TEXTURES\cubemaps\OutputCube.dds");
    }

    [Fact]
    public void ReadsTheUptimeFromProcessInfo()
    {
        Fixtures.Parse("renderer-init.log").Uptime.Should().Be(TimeSpan.FromMilliseconds(15021));
    }

    [Fact]
    public void StripsAddressSuffixesFromEngineSymbolsAndKeepsTheAddressLibraryId()
    {
        var frames = Fixtures.Parse(EnbA).Frames;

        frames[2].Should().Be(new CrashFrame(2, 'P', "SkyrimVR.exe", "0DC3592", "mov eax, [0x00006FFFFC220DB8]", "BSGraphics::SetDirtyStates", "75580"));
        frames[3].Symbol.Should().Be("Renderer::DrawIndexedTriangleList");
        frames[3].AddressLibraryId.Should().Be("75477");
    }

    [Fact]
    public void ReadsAnUnpaddedFrameIndex()
    {
        var frame = Fixtures.Parse("missing-masters.log").Frames[0];

        frame.Index.Should().Be(0);
        frame.Symbol.Should().Be("Actor::SetObjectReference");
        frame.AddressLibraryId.Should().Be("36457");
    }

    [Fact]
    public void ReadsAFrameWithAnIdButNoSymbol()
    {
        var frame = Fixtures.Parse(EnbA).Frames.Single(f => f.Index == 29);

        frame.Kind.Should().Be('S');
        frame.Module.Should().Be("SkyrimVR.exe");
        frame.Offset.Should().Be("392E310");
        frame.Symbol.Should().BeNull();
        frame.AddressLibraryId.Should().Be("528600");
        frame.Instruction.Should().Be("call 0x00006FFFFC9CE315");
    }

    [Fact]
    public void ReadsAFrameWithAnEmptyInstruction()
    {
        var frame = Fixtures.Parse("light-list-bread.log").Frames.Single(f => f.Index == 5);

        frame.Instruction.Should().BeNull();
        frame.Symbol.Should().Be("BSGeometry::AttachNearbyLights");
        frame.AddressLibraryId.Should().Be("99715");
    }

    [Fact]
    public void AGhidraAutoNameIsReportedAsAnUnnamedFunctionWithItsClass()
    {
        Fixtures.Parse("renderer-init.log").Frames[0].DisplayName.Should().Be("an unnamed engine function");
        Fixtures.Parse("missing-masters.log").Frames[2].DisplayName.Should().Be("an unnamed function of Main");
        Fixtures.Parse("script-vm.log").Frames.Single(f => f.Index == 19).DisplayName.Should().Be("an unnamed function of Actor");
        Fixtures.Parse(EnbA).Frames[2].DisplayName.Should().Be("BSGraphics::SetDirtyStates");
    }

    [Fact]
    public void AFrameWithoutASymbolIsNamedByItsModuleAndOffset()
    {
        var frames = Fixtures.Parse(EnbA).Frames;

        frames[0].DisplayName.Should().Be("d3d11.dll+00FCE60");
        frames[7].DisplayName.Should().Be("EngineFixes.dll+0054A77");
    }

    [Fact]
    public void KeepsAnSksePluginFrameWithItsSourcePathAndParams()
    {
        var frame = Fixtures.Parse(EnbA).Frames[7];

        frame.Module.Should().Be("EngineFixes.dll");
        frame.AddressLibraryId.Should().BeNull();
        frame.Symbol.Should().StartWith(@"D:\a\EngineFixesSkyrim64").And.Contain("| params:");
    }

    [Fact]
    public void CollectsRelevantObjectsWithTheirFileOrPlugin()
    {
        Fixtures.Parse(EnbA).RelevantObjects[0].Should()
            .Be(new RelevantObject("RSP+130", "BSTriShape", "LightWheel_05", null, @"Plants\GlowingMushroomCluster01.nif"));

        Fixtures.Parse("script-vm.log").RelevantObjects.Should()
            .Contain(new RelevantObject("RSP+300", "TESNPC", "Pelgurt", "0x0D18A9D2", "Yggdrasil - Synthesis Patch.esp"));
    }

    [Fact]
    public void AttachesAParentBlockToItsTopLevelObject()
    {
        var o = Fixtures.Parse(EnbA).ObjectDetails.Single(d => d.Location == "RSP+130");

        o.Parents.Should().Contain(new DetailLine("Name", "ENB Light 05"));
        o.Parents.Should().Contain(new DetailLine("RTTIName", "NiBillboardNode"));
        o.Own.Should().Contain(new DetailLine("RTTIName", "BSTriShape"));
        o.Own.Should().NotContain(new DetailLine("Name", "ENB Light 05"));
        o.Parenthetical.Should().Be(@"Plants\GlowingMushroomCluster01.nif");
    }

    [Fact]
    public void ReadsEffectTextureAndTextureLinesFromTheStackExpansion()
    {
        Fixtures.Parse(EnbA).ObjectDetails.Single(d => d.Location == "RSP+130").Own
            .Should().Contain(new DetailLine("EffectTexture[Source]", @"textures\effects\enb\fxBlack_32x32.dds"));

        Fixtures.Parse("light-list-bread.log").ObjectDetails.Single(d => d.Location == "RDI").Own
            .Should().Contain(new DetailLine("Texture[Diffuse]", @"textures\clutter\food\Bread01.dds"));
    }

    [Fact]
    public void ReadsThePluginAndModifiedByChainOfAnObject()
    {
        var own = Fixtures.Parse(EnbA).ObjectDetails.Single(d => d.Location == "RSP+130").Own;

        own.Should().Contain(new DetailLine("File", "Yggdrasil - Synthesis Patch.esp"));
        own.Should().Contain(l => l.Key == "Modified by"
            && l.Value.StartsWith("Skyrim.esm -> Complete Alchemy & Cooking Overhaul.esp"));
    }

    [Fact]
    public void ReadsRecentModelRequestsWhenPresent()
    {
        Fixtures.Parse("missing-masters.log").RecentModelRequests[0].Should().Be("Data/Meshes/questMarker_door.nif");
        Fixtures.Parse(EnbA).RecentModelRequests.Should().BeEmpty();
    }

    [Fact]
    public void ReadsResourcePathsWhenPresent()
    {
        Fixtures.Parse("water-ripples.log").ResourcePaths.Should().Equal(@"TEXTURES\cubemaps\OutputCube.dds");
        Fixtures.Parse(EnbA).ResourcePaths.Should().BeEmpty();
    }

    [Fact]
    public void ReadsTheSksePluginList()
    {
        var plugins = Fixtures.Parse(EnbA).SksePlugins;

        plugins.Should().Contain(new SksePlugin("EngineFixes.dll", "7.13"));
        plugins.Should().Contain(new SksePlugin("activeragdoll.dll", null));
    }

    [Fact]
    public void ReadsThePluginListAndItsTotal()
    {
        var report = Fixtures.Parse(EnbA);

        report.PluginCount.Should().Be(258);
        report.Plugins.Should().HaveCount(12);
        report.Plugins.Take(2).Should().Equal("Skyrim.esm", "Update.esm");
        report.Plugins.Should().Contain("Landscape and Water Fixes.esp");
    }

    [Fact]
    public void ReadsMissingMastersAfterThePluginListAndAZeroTotal()
    {
        var report = Fixtures.Parse("missing-masters.log");

        report.PluginCount.Should().Be(0);
        report.Plugins.Should().BeEmpty();
        report.MissingMasters.Should().Equal("SunHelmSurvival.esp");
    }

    [Fact]
    public void ABarePluginsSectionGivesNoCount()
    {
        var report = Fixtures.Parse("renderer-init.log");

        report.PluginCount.Should().BeNull();
        report.Plugins.Should().BeEmpty();
    }

    [Fact]
    public void ReadsTheGameState()
    {
        Fixtures.Parse(EnbA).GameState.Should().Contain("Main loop: active");
    }

    [Fact]
    public void SkipsASectionItDoesNotKnow()
    {
        var text = Fixtures.Text(EnbA).Replace("PLUGINS:", "SOMETHING NEW:\r\n\tfoo: bar\r\n\r\nPLUGINS:");
        text.Should().Contain("SOMETHING NEW:");

        new CrashLoggerSseFormat().Parse(text).PluginCount.Should().Be(258);
    }

    [Fact]
    public void DetectRejectsATrainwreckHeader()
    {
        CrashLogFormats.Detect(Fixtures.Text("unknown-format.log")).Should().BeNull();
        CrashLogFormats.Detect(Fixtures.Text(EnbA)).Should().BeOfType<CrashLoggerSseFormat>();
    }

    [Fact]
    public void DetectRejectsAnEmptyFile()
    {
        CrashLogFormats.Detect("").Should().BeNull();
    }

    [Fact]
    public void ATruncatedLogStillParses()
    {
        var text = Fixtures.Text(EnbA);
        var cut = text[..(text.Length * 6 / 10)];

        var act = () => new CrashLoggerSseFormat().Parse(cut);

        var report = act.Should().NotThrow().Subject;
        report.Frames.Count.Should().BeGreaterThan(0);
        report.ObjectDetails.Count.Should().BeGreaterThan(0);
    }
}
