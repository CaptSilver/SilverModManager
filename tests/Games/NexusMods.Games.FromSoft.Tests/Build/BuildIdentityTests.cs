using FluentAssertions;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Tests.Build;

public class BuildIdentityClassificationTests
{
    private static readonly BuildIdentity Stamped = new("2.7.1.0", "15000000");

    [Fact]
    public void NoStampIsUnchanged()
    {
        BuildIdentity.Classify(null, Stamped).Should().Be(BuildChangeKind.Unchanged);
        BuildIdentity.Drift(null, Stamped).Should().BeEmpty();
    }

    [Fact]
    public void AnIdenticalBuildIsUnchanged()
    {
        BuildIdentity.Classify(Stamped, Stamped with { }).Should().Be(BuildChangeKind.Unchanged);
        BuildIdentity.Drift(Stamped, Stamped with { }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("2.8.0.0", "15000000")]
    [InlineData("2.7.1.0", "15000001")]
    [InlineData("2.8.0.0", "15000001")]
    public void AMovedExeOrSteamBuildIdIsRepackagedWhileNoDataFieldIsTracked(string exe, string buildId)
    {
        BuildIdentity.Classify(Stamped, new BuildIdentity(exe, buildId)).Should().Be(BuildChangeKind.Repackaged);
    }

    [Fact]
    public void DriftListsEachMovedFieldWithItsOldAndNewValue()
    {
        var drift = BuildIdentity.Drift(Stamped, new BuildIdentity("2.8.0.0", "15000000"));
        drift.Should().Equal(new BuildChange("exe", "2.7.1.0", "2.8.0.0"));

        var both = BuildIdentity.Drift(Stamped, new BuildIdentity("2.8.0.0", "16000000"));
        both.Should().Equal(new BuildChange("exe", "2.7.1.0", "2.8.0.0"), new BuildChange("steam_buildid", "15000000", "16000000"));
    }

    // The tracked data fields are empty until regulation fields are added, which makes Patched and Tampered
    // unreachable through Classify. The full rule is still here, so these run it with a data field to keep it honest.
    [Fact]
    public void WithADataFieldTheFullRuleIsPatchedRepackagedTamperedOrUnchanged()
    {
        BuildField[] install = [BuildIdentity.ExeField];
        BuildField[] data = [BuildIdentity.SteamBuildIdField];
        var live = (string exe, string buildId) => new BuildIdentity(exe, buildId);

        BuildIdentity.Classify(Stamped, live("2.8.0.0", "16000000"), install, data).Should().Be(BuildChangeKind.Patched);
        BuildIdentity.Classify(Stamped, live("2.8.0.0", "15000000"), install, data).Should().Be(BuildChangeKind.Repackaged);
        BuildIdentity.Classify(Stamped, live("2.7.1.0", "16000000"), install, data).Should().Be(BuildChangeKind.Tampered);
        BuildIdentity.Classify(Stamped, live("2.7.1.0", "15000000"), install, data).Should().Be(BuildChangeKind.Unchanged);
        BuildIdentity.Classify(null, live("2.8.0.0", "16000000"), install, data).Should().Be(BuildChangeKind.Unchanged);
    }
}

public class BuildIdentityIdentifyTests(ITestOutputHelper helper) : AEldenRingTest<BuildIdentityIdentifyTests>(helper)
{
    [Fact]
    public void TheIdentityIsTheExecutablesFileVersionAndSteamsBuildId()
    {
        BuildIdentity.Identify(GameInstallation).Should().Be(new BuildIdentity("2.7.1.0", EldenRingInstall.SteamBuildId.ToString()));
    }

    [Fact]
    public void AnExecutableWithNoVersionResourceIsRefusedNamingTheFile()
    {
        File.WriteAllText((RealGameFolder / "eldenring.exe").GetFullPath(), "not a pe");

        var act = () => BuildIdentity.Identify(GameInstallation);

        act.Should().Throw<InvalidOperationException>().WithMessage("*eldenring.exe*");
    }

    [Fact]
    public void AMissingExecutableIsRefusedNamingTheFile()
    {
        File.Delete((RealGameFolder / "eldenring.exe").GetFullPath());

        var act = () => BuildIdentity.Identify(GameInstallation);

        act.Should().Throw<InvalidOperationException>().WithMessage("*eldenring.exe*");
    }

    [Fact]
    public void ABuildIdOfZeroIsRefusedNamingTheManifest()
    {
        EldenRingInstall.SetSteamBuildId(Install, "0");
        Relocate();

        var act = () => BuildIdentity.Identify(GameInstallation);

        act.Should().Throw<InvalidOperationException>().WithMessage("*appmanifest_1245620.acf*");
    }

    [Fact]
    public void AStoreThatReportsNoBuildIdIsRefusedNamingTheManifest()
    {
        var result = GameInstallation.LocatorResult;
        var withoutBuildId = new GameInstallation(new GameLocatorResult
        {
            Game = result.Game,
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
        }, GameInstallation.Locations);

        var act = () => BuildIdentity.Identify(withoutBuildId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*appmanifest_1245620.acf*");
    }
}
