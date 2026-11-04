using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Collections.Tests;

/// <summary>
/// An install run with <c>unattended</c> says a mod needs a person instead of opening the Advanced Manual Installer.
/// </summary>
public class UnattendedInstallJobTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<UnattendedInstallJobTests>(helper)
{
    private readonly CountingAdvancedInstallerHandler _manualInstaller = new();
    private readonly ScriptedGuidedInstallerState _guidedInstaller = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<IAdvancedInstallerHandler>(_manualInstaller)
            .AddTransient<IGuidedInstaller>(_ => _guidedInstaller.Create());
    }

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    private async Task<LibraryItem.ReadOnly> DocsOnlyArchive()
    {
        var (path, _) = await Offline.CreateMod(TemporaryFileManager.CreateFolder().Path, 0, ModShape.Unknown);
        var archive = await RegisterLocalArchive(path);
        return archive.AsLibraryFile().AsLibraryItem();
    }

    [Fact]
    public async Task Unattended_WithNothingClaimingTheArchive_ThrowsReviewRequired()
    {
        var loadout = await CreateLoadout();
        var item = await DocsOnlyArchive();

        var act = async () => await LoadoutManager.InstallItem(item, loadout, unattended: true);

        var exception = (await act.Should().ThrowAsync<ReviewRequiredException>()).Which;
        exception.Kind.Should().Be(ReviewKind.NoInstaller);
        exception.Reason.Should().Be("Archive contains no matching files");
        _manualInstaller.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unattended_FomodWithSteps_ThrowsNeedsChoicesWithoutOpeningAnyInstaller()
    {
        var loadout = await CreateLoadout();
        var archive = await RegisterLocalArchive(FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("TestCasesPacked/ComplexInstaller.fomod"));
        var item = archive.AsLibraryFile().AsLibraryItem();

        var act = async () => await LoadoutManager.InstallItem(item, loadout, unattended: true);

        (await act.Should().ThrowAsync<ReviewRequiredException>()).Which.Kind.Should().Be(ReviewKind.NeedsChoices);
        _guidedInstaller.Prompts.SelectMany(p => p.Steps).Should().BeEmpty();
        _manualInstaller.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Default_StillFallsBackToTheManualInstaller()
    {
        var loadout = await CreateLoadout();
        var item = await DocsOnlyArchive();

        var act = async () => await LoadoutManager.InstallItem(item, loadout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Found no installer");
        _manualInstaller.Calls.Should().Be(1);
    }
}
