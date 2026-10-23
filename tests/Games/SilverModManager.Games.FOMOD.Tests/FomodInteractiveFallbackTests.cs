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
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using FomodInterface = FomodInstaller.Interface;

namespace SilverModManager.Games.FOMOD.Tests;

/// <summary>
/// Saved choices that don't fit a FOMOD hand the install to the guided installer window, with the saved
/// choices selected, instead of failing it.
/// </summary>
public class FomodInteractiveFallbackTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<FomodInteractiveFallbackTests, Cyberpunk2077Game>(outputHelper)
{
    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ScriptedGuidedInstallerState _window = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddSingleton(_window)
            .AddTransient<IGuidedInstaller>(_ => _window.Create())
            .AddSingleton<FomodInterface.ICoreDelegates, InstallerDelegates>()
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"));
    }

    private static FomodOption[] Choices(string secondStepGroup = "Group 1") =>
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
                name = secondStepGroup,
                choices = [new FomodChoice { name = "Step 2 Group 1 Plugin 1" }, new FomodChoice { name = "Step 2 Group 1 Plugin 2" }],
            }],
        },
    ];

    private async Task<LoadoutItemGroup.ReadOnly> Install(FomodOption[] options)
    {
        var loadout = await CreateLoadout();
        var installer = Game.LibraryItemInstallers.OfType<FomodXmlInstaller>().Single();
        var path = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("TestCasesPacked/ComplexInstaller.fomod");
        var archive = await RegisterLocalArchive(path);

        using var tx = Connection.BeginTransaction();
        var loadoutGroup = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Complex", LoadoutId = loadout },
        };

        var result = await installer.ExecuteWithSavedChoicesAsync(archive, loadoutGroup, tx, loadout, options, "Complex mod", CancellationToken.None).AsTask().WaitAsync(Timeout);
        result.IsSuccess.Should().BeTrue();

        var dbResult = await tx.Commit();
        return dbResult.Remap(loadoutGroup);
    }

    private static string[] FileNames(LoadoutItemGroup.ReadOnly group) => group.Children
        .Select(x => ((GamePath)x.ToLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
        .Order()
        .ToArray();

    [Fact]
    public async Task ChoicesThatApply_InstallWithoutAPrompt()
    {
        var group = await Install(Choices());

        FileNames(group).Should().Equal("g1p1f1.out.esp", "g2p1f1.out.esp", "g2p2f1.out.esp");
        _window.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task ChoicesThatDoNotApply_PromptOnceWithTheSavedChoicesSelectedAndTheReason()
    {
        var group = await Install(Choices(secondStepGroup: "No such group"));

        var prompt = _window.Prompts.Should().ContainSingle().Which;
        prompt.Name.Should().Be("Sample Module");
        prompt.Notice.Should().Contain("Complex mod").And.Contain("saved choices").And.Contain("No such group");

        // The FOMOD recommends plugin 2 in the exactly-one group; the saved plugin 1 replaces it.
        var firstGroup = prompt.Steps[0].Groups.Single();
        firstGroup.Options.Single(o => o.Name == "Step 1 Group 1 Plugin 1").Type.Should().Be(OptionType.PreSelected);
        firstGroup.Options.Single(o => o.Name == "Step 1 Group 1 Plugin 2").Type.Should().Be(OptionType.Available);

        FileNames(group).Should().Equal("g1p1f1.out.esp", "g2p1f1.out.esp", "g2p2f1.out.esp");
    }

    [Fact]
    public async Task CancellingThePrompt_SkipsTheModWithTheReason()
    {
        _window.Cancel = true;

        var act = () => Install(Choices(secondStepGroup: "No such group"));

        var exception = (await act.Should().ThrowAsync<FomodInstallCancelledException>().WaitAsync(Timeout)).Which;
        exception.Message.Should().Contain("No such group");
        _window.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task AnInstallerWithoutUserInteraction_NeverPromptsAndFailsWithTheReason()
    {
        _window.SupportsUserInteraction = false;

        var act = () => Install(Choices(secondStepGroup: "No such group"));

        await act.Should().ThrowAsync<SavedChoicesDoNotApplyException>().WaitAsync(Timeout);
        _window.Prompts.Should().BeEmpty();
    }
}
