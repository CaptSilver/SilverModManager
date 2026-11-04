using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Games.FOMOD;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.App.Mcp;
using SilverModManager.Collections;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What <c>install-collection</c> prints once the install has run. The verb's download step needs the network, so these
/// call the reporting step on a collection built from local archives.
/// </summary>
[Collection("ConsoleOutput")]
public class InstallCollectionVerbTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<InstallCollectionVerbTests>(helper)
{
    private readonly CountingAdvancedInstallerHandler _manualInstaller = new();

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<IAdvancedInstallerHandler>(_manualInstaller);

    private Task<(NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items)> CreateCollection(
        (FomodOption[]? Choices, string Name)[] mods, ModShape[] shapes) =>
        new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager).Create(mods, shapes: shapes);

    private static FomodOption[] Choices() =>
    [
        new FomodOption { name = "Step 1", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option A" }] }] },
        new FomodOption { name = "Step 2", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option C" }] }] },
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Run_PrintsTheNeedReviewTable_ExitsZero_AndNeverOpensTheManualInstaller()
    {
        var (file, revision, _) = await CreateCollection([(null, "Unknown mod"), (Choices(), "Fomod mod")], [ModShape.Unknown, ModShape.Fomod]);
        var loadout = await CreateLoadout();
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, CancellationToken.None).WaitAsync(Timeout);

        code.Should().Be(0, renderer.Output);
        renderer.Output.Should().Contain("Need review").And.Contain("Unknown mod").And.Contain("NoInstaller")
            .And.Contain("Open the collection in the app");
        renderer.Output.Should().NotContain("Fomod mod");
        renderer.Output.Should().NotContain("Not installed");
        _manualInstaller.Calls.Should().Be(0);
        var group = CollectionDownloader.GetCollectionGroup(revision, loadout.LoadoutId, Connection.Db).Value;
        PendingInstalls.For(Connection.Db, group.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Run_ExitsOne_WhenAModFailed()
    {
        var (file, revision, _) = await CreateCollection([(null, "Broken mod")], [ModShape.Plain]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod0.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("broken on purpose");
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, CancellationToken.None).WaitAsync(Timeout);

        code.Should().Be(1, renderer.Output);
        renderer.Output.Should().Contain("Not installed").And.Contain("Broken mod").And.Contain("broken on purpose");
    }
}
