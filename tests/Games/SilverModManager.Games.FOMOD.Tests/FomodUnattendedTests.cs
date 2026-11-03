using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using FomodInterface = FomodInstaller.Interface;

namespace SilverModManager.Games.FOMOD.Tests;

/// <summary>
/// An unattended FOMOD install never asks anyone: a step that needs an answer ends it with
/// <see cref="ReviewRequiredException"/> and nothing is written.
/// </summary>
public class FomodUnattendedTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<FomodUnattendedTests, Cyberpunk2077Game>(outputHelper)
{
    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string RequiredFilesOnly = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
            <moduleName>No steps</moduleName>
            <requiredInstallFiles>
                <file source="plain.esp" destination="plain.out.esp"/>
            </requiredInstallFiles>
        </config>
        """;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<FomodInterface.ICoreDelegates, InstallerDelegates>()
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"));
    }

    private FomodXmlInstaller GameInstaller() => Game.LibraryItemInstallers.OfType<FomodXmlInstaller>().Single();

    private async Task<LibraryArchive.ReadOnly> ComplexFomod() =>
        await RegisterLocalArchive(FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("TestCasesPacked/ComplexInstaller.fomod"));

    private (LoadoutItemGroup.New Group, IMainTransaction Tx) NewGroup(Loadout.ReadOnly loadout)
    {
        var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Mod", LoadoutId = loadout },
        };
        return (group, tx);
    }

    [Fact]
    public async Task ExecuteUnattended_ThrowsAtTheFirstStep_AndWritesNothing()
    {
        var loadout = await CreateLoadout();
        var archive = await ComplexFomod();
        var (group, tx) = NewGroup(loadout);
        using var _ = tx;

        var act = async () => await GameInstaller()
            .ExecuteUnattendedAsync(archive.AsLibraryFile().AsLibraryItem(), group, tx, loadout, CancellationToken.None)
            .AsTask().WaitAsync(Timeout);

        (await act.Should().ThrowAsync<ReviewRequiredException>()).Which.Kind.Should().Be(ReviewKind.NeedsChoices);

        var committed = await tx.Commit();
        committed.Remap(group).Children.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteUnattended_InstallsAFomodWithoutSteps()
    {
        var loadout = await CreateLoadout();
        var archive = await AddArchiveWithContents("plain.zip",
            ("fomod/ModuleConfig.xml", RequiredFilesOnly),
            ("plain.esp", "plain"));
        var (group, tx) = NewGroup(loadout);
        using var _ = tx;

        var result = await GameInstaller()
            .ExecuteUnattendedAsync(archive.AsLibraryFile().AsLibraryItem(), group, tx, loadout, CancellationToken.None)
            .AsTask().WaitAsync(Timeout);

        result.IsSuccess.Should().BeTrue();
        var committed = await tx.Commit();
        committed.Remap(group).Children
            .Select(x => ((GamePath)x.ToLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
            .Should().Equal("plain.out.esp");
    }

    [Fact]
    public async Task ExecuteWithSavedChoices_WithoutPrompting_ThrowsReviewRequired()
    {
        var loadout = await CreateLoadout();
        var archive = await ComplexFomod();
        var (group, tx) = NewGroup(loadout);
        using var _ = tx;
        FomodOption[] stale =
        [
            new FomodOption
            {
                name = "Step 1",
                groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "No such option" }] }],
            },
        ];

        var act = async () => await GameInstaller()
            .ExecuteWithSavedChoicesAsync(archive, group, tx, loadout, stale, "Complex mod", allowPrompt: false, CancellationToken.None)
            .AsTask().WaitAsync(Timeout);

        var exception = (await act.Should().ThrowAsync<ReviewRequiredException>()).Which;
        exception.Kind.Should().Be(ReviewKind.NeedsChoices);
        exception.Reason.Should().Contain("No such option");
        exception.InnerException.Should().BeOfType<SavedChoicesDoNotApplyException>();
    }
}
