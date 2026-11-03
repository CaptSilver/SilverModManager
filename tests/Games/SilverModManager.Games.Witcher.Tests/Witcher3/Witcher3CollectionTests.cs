using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FOMOD;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Collections;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

/// <summary>A Witcher 3 collection installs its FOMOD mods from the choices the author saved, without asking anyone.</summary>
public class Witcher3CollectionTests(ITestOutputHelper outputHelper) : AWitcher3ArchiveTest<Witcher3CollectionTests>(outputHelper)
{
    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AWitcher3CollectionModWithSavedFomodChoices_Installs()
    {
        FomodOption[] choices =
        [
            new FomodOption { name = "Step 1", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option B" }] }] },
            new FomodOption { name = "Step 2", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option C" }] }] },
        ];
        var (file, revision, items) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager, GameDomain.From("witcher3"))
            .Create([(choices, "W3 fomod mod")]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        var group = await Run().WaitAsync(Timeout);

        var installed = LoadoutItemGroup.Load(Connection.Db, group.Id).Children.OfTypeLoadoutItemGroup().Should().ContainSingle().Which;
        Targets(installed).Should().Equal("{Game}/mod0_b.out.esp", "{Game}/mod0_c.out.esp");
    }
}
