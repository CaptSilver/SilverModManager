using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using FomodInterface = FomodInstaller.Interface;

namespace SilverModManager.Games.FOMOD.Tests;

/// <summary>
/// Installs FOMODs with saved choices through the real <see cref="InstallerDelegates"/>, the way a
/// collection install does, instead of the mock delegates the other FOMOD tests use.
/// </summary>
public class FomodSavedChoicesTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<FomodSavedChoicesTests, Cyberpunk2077Game>(outputHelper)
{
    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<FomodInterface.ICoreDelegates, InstallerDelegates>()
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"));
    }

    private static FomodOption[] ValidChoices() =>
    [
        new FomodOption
        {
            name = "Step 1",
            groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Step 1 Group 1 Plugin 1" }] }],
        },
        new FomodOption
        {
            name = "Step 2",
            groups = [new FomodGroup
            {
                name = "Group 1",
                choices = [new FomodChoice { name = "Step 2 Group 1 Plugin 1" }, new FomodChoice { name = "Step 2 Group 1 Plugin 2" }],
            }],
        },
    ];

    private FomodXmlInstaller GameInstaller() => Game.LibraryItemInstallers.OfType<FomodXmlInstaller>().Single();

    private async Task<LoadoutItemGroup.ReadOnly> InstallWithChoices(FomodXmlInstaller installer, Loadout.ReadOnly loadout, string testCase, FomodOption[] options, CancellationToken cancellationToken = default)
    {
        var path = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine($"TestCasesPacked/{testCase}.fomod");
        var archive = await RegisterLocalArchive(path);

        using var tx = Connection.BeginTransaction();
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();
        var loadoutGroup = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId)
            {
                Name = libraryItem.Name,
                LoadoutId = loadout,
            },
        };

        var result = await installer.ExecuteAsync(archive, loadoutGroup, tx, loadout, options, cancellationToken);
        result.IsSuccess.Should().BeTrue();

        var dbResult = await tx.Commit();
        return dbResult.Remap(loadoutGroup);
    }

    private static string[] FileNames(LoadoutItemGroup.ReadOnly group) => group.Children
        .Select(x => ((GamePath)x.ToLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
        .Order()
        .ToArray();

    [Fact]
    public async Task SavedChoices_ApplyToTheOneInstall()
    {
        var loadout = await CreateLoadout();
        var group = await InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", ValidChoices()).WaitAsync(Timeout);
        FileNames(group).Should().Equal("g1p1f1.out.esp", "g2p1f1.out.esp", "g2p2f1.out.esp");
    }

    [Fact]
    public async Task ConcurrentInstallsOnOneInstaller_AllFinishWithTheirOwnChoices()
    {
        var loadout = await CreateLoadout();
        var installer = GameInstaller();

        // Same shape as a collection install: many FOMOD mods started at once on the game's one installer.
        var cases = new[] { "ComplexInstaller", "WithImages", "ComplexInstaller", "WithImages", "ComplexInstaller", "WithImages", "ComplexInstaller", "WithImages" };
        var installs = cases.Select(c => InstallWithChoices(installer, loadout, c, ValidChoices())).ToArray();

        var groups = await Task.WhenAll(installs).WaitAsync(Timeout);
        foreach (var group in groups)
            FileNames(group).Should().Equal("g1p1f1.out.esp", "g2p1f1.out.esp", "g2p2f1.out.esp");
    }

    [Fact]
    public async Task SavedChoicesWithFewerStepsThanTheFomod_Fail()
    {
        var loadout = await CreateLoadout();
        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", ValidChoices()[..1]);
        await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SavedChoicesNamingAMissingOption_Fail()
    {
        var loadout = await CreateLoadout();
        var choices = ValidChoices();
        choices[0].groups[0].choices[0] = new FomodChoice { name = "No such plugin" };

        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", choices);
        await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SavedChoicesNamingAMissingGroup_Fail()
    {
        var loadout = await CreateLoadout();
        var choices = ValidChoices();
        choices[0].groups[0] = new FomodGroup { name = "No such group", choices = choices[0].groups[0].choices };

        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", choices);
        await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SavedChoicesFailure_NamesTheStepGroupAndOption()
    {
        var loadout = await CreateLoadout();
        var choices = ValidChoices();
        choices[1].groups[0].choices[1] = new FomodChoice { name = "No such plugin" };

        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", choices);
        var exception = (await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout)).Which;
        exception.StepName.Should().Be("Step 2");
        exception.GroupName.Should().Be("Group 1");
        exception.OptionName.Should().Be("No such plugin");
    }

    [Fact]
    public async Task SavedChoicesBreakingTheGroupType_Fail()
    {
        var loadout = await CreateLoadout();
        var choices = ValidChoices();

        // Step 1 Group 1 is SelectExactlyOne
        choices[0].groups[0] = new FomodGroup
        {
            name = "Group 1",
            choices = [new FomodChoice { name = "Step 1 Group 1 Plugin 1" }, new FomodChoice { name = "Step 1 Group 1 Plugin 2" }],
        };

        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", choices);
        var exception = (await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout)).Which;
        exception.StepName.Should().Be("Step 1");
        exception.GroupName.Should().Be("Group 1");
    }

    [Fact]
    public async Task SavedSteps_AreMatchedByNameNotPosition()
    {
        var loadout = await CreateLoadout();
        var choices = ValidChoices().Reverse().ToArray();

        var group = await InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", choices).WaitAsync(Timeout);
        FileNames(group).Should().Equal("g1p1f1.out.esp", "g2p1f1.out.esp", "g2p2f1.out.esp");
    }

    [Fact]
    public async Task CancellingTheInstall_EndsItInsteadOfWaitingForTheFomod()
    {
        var loadout = await CreateLoadout();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => InstallWithChoices(GameInstaller(), loadout, "ComplexInstaller", ValidChoices(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>().WaitAsync(Timeout);
    }
}
