using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

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

public sealed class BuildIdentityOnAnotherLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("build-identity").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameInstallation TestSoulsInstallation(string? buildId, bool withExecutable = true)
    {
        if (withExecutable)
        {
            Directory.CreateDirectory(Path.Combine(_root, "Bin"));
            File.WriteAllBytes(Path.Combine(_root, "Bin", "testsouls.exe"), FakePe.WithVersion(new Version(1, 2, 3, 4)));
        }

        // NSubstitute does not run default interface members, so Layout is configured beside Definition.
        var game = Substitute.For<IFromSoftGame>();
        game.Definition.Returns(TestSoulsDefinition.Definition);
        game.Layout.Returns(TestSoulsDefinition.Layout);
        game.DisplayName.Returns("Test Souls");

        var root = FileSystem.Shared.FromUnsanitizedFullPath(_root);
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = root,
            Store = GameStore.Steam,
            StoreIdentifier = "424242",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            StoreBuildId = buildId,
        };
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, root } }.ToImmutableDictionary());
        return new GameInstallation(locatorResult, locations);
    }

    [Fact]
    public void TheVersionIsReadFromTheLayoutsExecutableInItsSubfolder()
    {
        BuildIdentity.Identify(TestSoulsInstallation("777")).Should().Be(new BuildIdentity("1.2.3.4", "777"));
    }

    [Fact]
    public void AMissingBuildIdNamesTheLayoutsManifestAndTheGame()
    {
        var act = () => BuildIdentity.Identify(TestSoulsInstallation("0"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*appmanifest_424242.acf*Test Souls*")
            .And.Message.Should().NotContain("Elden Ring");
    }

    [Fact]
    public void AMissingExecutableNamesTheLayoutsExecutable()
    {
        var act = () => BuildIdentity.Identify(TestSoulsInstallation("777", withExecutable: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*testsouls.exe*")
            .And.Message.Should().NotContain("eldenring.exe");
    }

    [Fact]
    public void AGameThatIsNotFromSoftIsRefusedNamingIt()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Other Game");
        var installation = TestSoulsInstallation("777");
        var result = installation.LocatorResult;
        installation = new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            StoreBuildId = result.StoreBuildId,
        }, installation.Locations);

        var act = () => BuildIdentity.Identify(installation);

        act.Should().Throw<InvalidOperationException>().WithMessage("Other Game is not a FromSoft game*");
    }
}

public class BuildStampModelTests
{
    [Fact]
    public void TheStampKeepsItsPersistedAttributeNamespace()
    {
        FromSoftBuildStamp.Exe.Id.Namespace.Should().Be("SilverModManager.FromSoft.BuildStamp");
        FromSoftBuildStamp.Installation.Id.Namespace.Should().Be("SilverModManager.FromSoft.BuildStamp");
        FromSoftBuildStamp.SteamBuildId.Id.Namespace.Should().Be("SilverModManager.FromSoft.BuildStamp");
        FromSoftBuildStamp.RecordedAt.Id.Namespace.Should().Be("SilverModManager.FromSoft.BuildStamp");
    }
}
