using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.CreationEngine.CrashLogs;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

public class CrashSignatureTests
{
    private static CrashLogFile At(DateTime t) => new(default, t, Size.Zero);

    private static CrashFrame Frame(int index, string module, string offset, string? id) =>
        new(index, 'P', module, offset, null, null, id);

    [Fact]
    public void TheTwoEnbLogsShareASignatureDespiteDifferentD3d11Offsets()
    {
        var a = CrashSignature.Of(Fixtures.Parse("enb-light-mushroom-a.log"));
        var b = CrashSignature.Of(Fixtures.Parse("enb-light-mushroom-b.log"));

        a.Should().Be(b);
        a.Should().Be(new CrashSignature("d3d11.dll", "75580,75477,100847", CrashLogPaths.Classify(@"Plants\GlowingMushroomCluster01.nif")));
    }

    [Fact]
    public void TheSignatureIgnoresTheExecutableBaseAddress()
    {
        var text = Fixtures.Text("enb-light-mushroom-a.log");
        text.Should().Contain("0x6FFFF");
        var moved = new CrashLoggerSseFormat().Parse(text.Replace("0x6FFFF", "0x00014"));

        CrashSignature.Of(moved).Should().Be(CrashSignature.Of(Fixtures.Parse("enb-light-mushroom-a.log")));
    }

    [Fact]
    public void TwoUnnamedEngineFunctionsWithDifferentIdsDoNotShareASignature()
    {
        CrashSignature.Of(Fixtures.Parse("renderer-init.log"))
            .Should().NotBe(CrashSignature.Of(Fixtures.Parse("light-list-bread.log")));
    }

    [Fact]
    public void ThreeFramesAtOneIdStayDistinctFromTheEnbSignature()
    {
        var scriptVm = CrashSignature.Of(Fixtures.Parse("script-vm.log"));

        scriptVm.Should().NotBe(CrashSignature.Of(Fixtures.Parse("enb-light-mushroom-a.log")));
        scriptVm.EngineFrames.Should().Be("52662,52662,52662");
    }

    [Fact]
    public void TheFaultModuleKeepsAnEngineCrashApartFromAD3d11CrashAtTheSameId()
    {
        var engine = CrashSignature.Of(Fixtures.Parse("renderer-init.log"));
        var enb = CrashSignature.Of(Fixtures.Parse("enb-light-mushroom-a.log"));

        engine.EngineFrames.Should().StartWith("75580");
        enb.EngineFrames.Should().StartWith("75580");
        engine.Should().NotBe(enb);
    }

    [Fact]
    public void AnEspRelevantObjectIsNotTheSubjectFile()
    {
        CrashSignature.Of(Fixtures.Parse("script-vm.log")).SubjectFile.Should().BeNull();
    }

    [Fact]
    public void AFrameWithoutAnAddressLibraryIdFallsBackToTheExecutableOffset()
    {
        var report = new CrashReport
        {
            FaultModule = "SkyrimVR.exe",
            Frames = [Frame(0, "SkyrimVR.exe", "0DC3592", null), Frame(1, "SkyrimVR.exe", "0DBDD68", "75477")],
        };

        CrashSignature.Of(report).EngineFrames.Should().Be("SkyrimVR.exe+0DC3592,75477");
    }

    [Fact]
    public void OnlyTheFirstThreeEngineFramesAndOnlyProbableOnesCount()
    {
        var report = new CrashReport
        {
            FaultModule = "SkyrimVR.exe",
            Frames =
            [
                Frame(0, "d3d11.dll", "00FCE60", null),
                Frame(1, "SkyrimVR.exe", "1", "10"),
                new CrashFrame(2, 'S', "SkyrimVR.exe", "2", null, null, "20"),
                Frame(3, "SkyrimVR.exe", "3", "30"),
                Frame(4, "SkyrimVR.exe", "4", "40"),
                Frame(5, "SkyrimVR.exe", "5", "50"),
            ],
        };

        CrashSignature.Of(report).EngineFrames.Should().Be("10,30,40");
    }

    [Fact]
    public void AReportWithNoEngineFramesStillHasASignature()
    {
        CrashReport Foreign() => new()
        {
            FaultModule = "d3d11.dll",
            Frames = [Frame(0, "d3d11.dll", "00FCE60", null), Frame(1, "d3d11.dll", "01010C1", null)],
        };

        var signature = CrashSignature.Of(Foreign());
        signature.EngineFrames.Should().Be("");
        signature.FaultModule.Should().Be("d3d11.dll");
        signature.Should().Be(CrashSignature.Of(Foreign()));
    }

    [Fact]
    public void AReportWithoutAFaultModuleHasAnEmptyOne()
    {
        CrashSignature.Of(new CrashReport()).FaultModule.Should().Be("");
    }

    [Fact]
    public void GroupsCountAndKeepTheNewestReport()
    {
        var t1 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);
        var t3 = t1.AddHours(2);
        var enbA = Fixtures.Parse("enb-light-mushroom-a.log");
        var enbB = Fixtures.Parse("enb-light-mushroom-b.log");
        var bread = Fixtures.Parse("light-list-bread.log");

        var groups = CrashGroups.Of([(At(t1), enbA), (At(t2), enbB), (At(t3), bread)]);

        groups.Should().HaveCount(2);
        groups[0].Representative.Should().BeSameAs(bread);
        groups[0].Count.Should().Be(1);
        var enb = groups[1];
        enb.Count.Should().Be(2);
        enb.Representative.Should().BeSameAs(enbB);
        enb.Newest.LastWriteUtc.Should().Be(t2);
        enb.FirstSeen.Should().Be(t1);
        enb.LastSeen.Should().Be(t2);
        enb.Signature.Should().Be(CrashSignature.Of(enbA));
    }

    [Fact]
    public void TheNewestReportWinsWhateverTheOrderTheyArriveIn()
    {
        var t1 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var enbA = Fixtures.Parse("enb-light-mushroom-a.log");
        var enbB = Fixtures.Parse("enb-light-mushroom-b.log");

        var groups = CrashGroups.Of([(At(t1.AddHours(1)), enbB), (At(t1), enbA)]);

        groups.Should().ContainSingle().Which.Representative.Should().BeSameAs(enbB);
    }

    [Fact]
    public void NoReportsMakeNoGroups()
    {
        CrashGroups.Of([]).Should().BeEmpty();
    }
}
