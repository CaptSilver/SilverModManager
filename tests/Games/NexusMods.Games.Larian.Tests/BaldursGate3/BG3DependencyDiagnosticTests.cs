using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.Larian.BaldursGate3.Emitters;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// Dependency warnings for paks, with one module shipped by the game's own Data folder.
/// </summary>
public class BG3DependencyDiagnosticTests(ITestOutputHelper outputHelper) : ADependencyDiagnosticTests(outputHelper)
{
    protected override Dictionary<RelativePath, byte[]>? GameFiles { get; } = new()
    {
        { "Data/Shipped.pak", SyntheticPak.ForModule("ShippedModule", "ShippedModule", ShippedUuid, SyntheticPak.Version(4, 8, 100)) },
    };

    [Fact]
    public async Task ADependencyOnAModuleTheGameShipsIsSatisfied()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Some Mod", "some.pak", SyntheticPak.ForModule("Some", "Some", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("ShippedModule", ShippedUuid, SyntheticPak.Version(4, 8, 30))));

        (await Dependencies(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADependencyNothingProvidesStillWarns()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Some Mod", "some.pak", SyntheticPak.ForModule("Some", "Some", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("Nowhere", MissingUuid, SyntheticPak.Version(1))));

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        warning.Id.Should().Be(MissingDependencyId);
        Summary(warning).Should().Be("The mod Some Mod is missing the required dependency 'Nowhere' v1.0.0+.");
    }

    [Fact]
    public async Task ADependencyAnotherEnabledPakProvidesIsSatisfied()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Library", "library.pak", SyntheticPak.ForModule("Library", "Library", MissingUuid, SyntheticPak.Version(2)));
        await AddPak(loadout, "Some Mod", "some.pak", SyntheticPak.ForModule("Some", "Some", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("Library", MissingUuid, SyntheticPak.Version(1))));

        (await Dependencies(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task APakInTheOverridesGroupNamesItsModuleInTheWarning()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Overrides", "mindweaver.pak", SyntheticPak.ForModule("Mindweaver", "Mindweaver", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("Nowhere", MissingUuid, SyntheticPak.Version(1))), inOverrides: true);

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        Summary(warning).Should().Be("The mod Mindweaver (in Overrides) is missing the required dependency 'Nowhere' v1.0.0+.");
    }

    [Fact]
    public async Task AGameModuleOlderThanRequiredIsReported()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Some Mod", "some.pak", SyntheticPak.ForModule("Some", "Some", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("ShippedModule", ShippedUuid, SyntheticPak.Version(5))));

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        warning.Id.Should().Be(OutdatedGameModuleId);
        Summary(warning).Should().Be("The mod Some (in Some Mod) requires at least version 5.0.0+ of the game module 'ShippedModule' but the game has v4.8.100.");
    }
}

/// <summary>
/// With no readable game folder the list of known game modules stands in for it.
/// </summary>
public class BG3DependencyFallbackDiagnosticTests(ITestOutputHelper outputHelper) : ADependencyDiagnosticTests(outputHelper)
{
    protected override Dictionary<RelativePath, byte[]>? GameFiles => null;

    [Fact]
    public async Task TheKnownGameModulesAreIgnoredAndOthersStillWarn()
    {
        var loadout = await CreateLoadout();
        await AddPak(loadout, "Some Mod", "some.pak", SyntheticPak.ForModule("Some", "Some", OtherUuid, SyntheticPak.Version(1),
            new SyntheticPak.Dependency("GustavX", GustavXUuid, SyntheticPak.Version(4, 8, 100)),
            new SyntheticPak.Dependency("Gustav", "991c9c7a-fb80-40cb-8f0d-b92d4e80e9b1", SyntheticPak.Version(4)),
            new SyntheticPak.Dependency("Nowhere", MissingUuid, SyntheticPak.Version(1))));

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        Summary(warning).Should().Contain("'Nowhere'");
    }
}

public abstract class ADependencyDiagnosticTests(ITestOutputHelper outputHelper)
    : ALibraryArchiveInstallerTests<ADependencyDiagnosticTests, Larian.BaldursGate3.BaldursGate3>(outputHelper)
{
    protected const string ShippedUuid = "11111111-1111-4111-8111-111111111111";
    protected const string OtherUuid = "22222222-2222-4222-8222-222222222222";
    protected const string MissingUuid = "33333333-3333-4333-8333-333333333333";
    protected const string GustavXUuid = "cb555efe-2d9e-131f-8195-a89329d218ea";

    protected static readonly DiagnosticId MissingDependencyId = new("NexusMods.Games.Larian.BaldursGate3", number: 1);
    protected static readonly DiagnosticId OutdatedGameModuleId = new("NexusMods.Games.Larian.BaldursGate3", number: 6);

    protected abstract Dictionary<RelativePath, byte[]>? GameFiles { get; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddBaldursGate3()
            .AddDiagnosticWriter()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Larian.BaldursGate3.BaldursGate3>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                GameFiles,
                stores: [GameStore.Steam]
            )
            {
                // Steam locator ids are numeric build ids, which the hashes queries cast; the stub hasher knows this one.
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    protected string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    protected async Task<List<Diagnostic>> Dependencies(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        var emitter = Game.DiagnosticEmitters.OfType<DependencyDiagnosticEmitter>().Single();
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();
        return diagnostics;
    }

    /// <summary>Adds a group holding one pak in the game's mods folder, backed up in the file store.</summary>
    protected async Task AddPak(Loadout.ReadOnly loadout, string groupName, string fileName, byte[] pak, bool inOverrides = false)
    {
        var hash = pak.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(fileName, new MemoryStream(pak)), hash, Size.FromLong(pak.Length))]);

        using var tx = Connection.BeginTransaction();
        EntityId groupId;
        if (inOverrides)
        {
            // Creating a loadout already makes the overrides group.
            groupId = (EntityId)LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single().Id.Value;
        }
        else
        {
            groupId = AddEmptyGroup(tx, loadout.Id, groupName).Value;
        }

        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    LoadoutId = loadout.Id,
                    ParentId = LoadoutItemGroupId.From(groupId),
                    Name = fileName,
                },
                TargetPath = new GamePath(Bg3Constants.ModsLocationId, fileName).ToGamePathParentTuple(loadout.Id),
            },
            Hash = hash,
            Size = Size.FromLong(pak.Length),
        };
        await tx.Commit();
    }
}
