using FluentAssertions;
using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;
using SilverModManager.Games.Larian.BaldursGate3.ModList;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.Modsettings;

public class Bg3ModListPlanTests
{
    private static ModsettingsDocument.ModuleEntry Module(string uuid, string name = "Module") =>
        new(Folder: name, Name: name, PublishHandle: "0", Uuid: uuid, Version64: "1", Md5: "");

    private static Bg3PakCandidate Pak(string name, string uuid, bool active = true, bool gameInstalled = false) =>
        new(name, gameInstalled, active, Module(uuid, name));

    [Fact]
    public void ActivePaksKeepTheirOrder()
    {
        var plan = Bg3ModListPlan.Build([Pak("b.pak", "2"), Pak("a.pak", "1"), Pak("c.pak", "3")]);

        plan.Names.Should().Equal("b.pak", "a.pak", "c.pak");
        plan.Duplicates.Should().BeEmpty();
    }

    [Fact]
    public void InactivePaksAreLeftOut()
    {
        var plan = Bg3ModListPlan.Build([Pak("a.pak", "1"), Pak("b.pak", "2", active: false), Pak("c.pak", "3")]);

        plan.Names.Should().Equal("a.pak", "c.pak");
    }

    [Fact]
    public void APakThatCouldNotBeReadIsLeftOut()
    {
        var plan = Bg3ModListPlan.Build([Pak("a.pak", "1"), new Bg3PakCandidate("broken.pak", false, true, null)]);

        plan.Names.Should().Equal("a.pak");
    }

    [Fact]
    public void TheAppsCopyOfADuplicateIsWrittenWhateverTheOrder()
    {
        var plan = Bg3ModListPlan.Build([
            Pak("game-ab12.pak", "1", gameInstalled: true),
            Pak("mid.pak", "2"),
            Pak("mine.pak", "1"),
        ]);

        plan.Names.Should().Equal("mid.pak", "mine.pak");
        plan.Duplicates.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new Bg3DuplicateModule("1", "mine.pak", "mine.pak", ["game-ab12.pak"]));
    }

    [Fact]
    public void TheEarlierOfTwoCopiesFromTheSameSourceIsWritten()
    {
        var plan = Bg3ModListPlan.Build([Pak("first.pak", "1"), Pak("second.pak", "1")]);

        plan.Names.Should().Equal("first.pak");
        plan.Duplicates.Single().IgnoredPaks.Should().Equal("second.pak");
    }

    [Fact]
    public void AnInactiveCopyIsNotADuplicate()
    {
        var plan = Bg3ModListPlan.Build([Pak("a.pak", "1"), Pak("a-off.pak", "1", active: false)]);

        plan.Names.Should().Equal("a.pak");
        plan.Duplicates.Should().BeEmpty();
    }
}
