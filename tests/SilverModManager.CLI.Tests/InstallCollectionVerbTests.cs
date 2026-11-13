using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Loadouts.Verbs;
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

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, json: false, CancellationToken.None).WaitAsync(Timeout);

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

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, json: false, CancellationToken.None).WaitAsync(Timeout);

        code.Should().Be(1, renderer.Output);
        renderer.Output.Should().Contain("Not installed").And.Contain("Broken mod").And.Contain("Something went wrong. Details are in the log at");
        renderer.Output.Should().NotContain("broken on purpose");
    }

    [Fact]
    public async Task Json_ListsInstalledAndPending()
    {
        var (file, revision, _) = await CreateCollection([(null, "Unknown mod"), (Choices(), "Fomod mod")], [ModShape.Unknown, ModShape.Fomod]);
        var loadout = await CreateLoadout();
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, json: true, CancellationToken.None).WaitAsync(Timeout);

        code.Should().Be(0, renderer.Output);
        var root = JsonDocument.Parse(renderer.Output).RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("installed", "pending", "missingLinks", "failed");
        root.GetProperty("installed").EnumerateArray().Select(e => e.GetString()).Should().Equal("Fomod mod");
        var pending = root.GetProperty("pending").EnumerateArray().Should().ContainSingle().Subject;
        pending.GetProperty("name").GetString().Should().Be("Unknown mod");
        pending.GetProperty("kind").GetString().Should().Be("NoInstaller");
        pending.TryGetProperty("reason", out _).Should().BeTrue();
        root.GetProperty("missingLinks").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Json_ListsAFailedMod_AndExitsOne()
    {
        var (file, revision, _) = await CreateCollection([(null, "Broken mod")], [ModShape.Plain]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod0.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("broken on purpose");
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.Run(renderer, ServiceProvider, loadout, file, revision, json: true, CancellationToken.None).WaitAsync(Timeout);

        code.Should().Be(1, renderer.Output);
        var failed = JsonDocument.Parse(renderer.Output).RootElement.GetProperty("failed").EnumerateArray().Should().ContainSingle().Subject;
        failed.GetProperty("name").GetString().Should().Be("Broken mod");
        renderer.Output.Should().NotContain("broken on purpose");
    }

    [Fact]
    public async Task FreeAccount_IsReadFromTheLiveCheck()
    {
        var login = Substitute.For<ILoginManager>();
        login.IsPremium.Returns(true);
        login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(new UserInfo { UserId = SilverModManager.Abstractions.NexusWebApi.Types.UserId.From(1), Name = "free", UserRole = UserRole.Free, AvatarUrl = null }));

        (await CollectionInstallReport.IsPremium(login, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AccountCheckThrows_SaysNexusDidNotAnswer()
    {
        var login = Substitute.For<ILoginManager>();
        login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns<ValueTask<UserInfo?>>(_ => throw new HttpRequestException("connection refused"));

        var act = async () => await CollectionInstallReport.IsPremium(login, CancellationToken.None);

        (await act.Should().ThrowAsync<LoadoutVerbRefusedException>()).Which.Message.Should().Contain("Nexus Mods did not answer").And.Contain("connection refused");
    }

    [Fact]
    public async Task FreeAccount_WithMissingLinks_ExitsTwo()
    {
        var (_, _, items) = await CreateCollection([(null, "Mod one")], [ModShape.Plain]);
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.ReportMissingLinks(renderer, [(items[0], new Uri("https://www.nexusmods.com/x/mods/1?tab=files"))], json: false);

        code.Should().Be(2);
        renderer.Output.Should().Contain("Missing 1 downloads").And.Contain("https://www.nexusmods.com/x/mods/1");
    }

    [Fact]
    public async Task FreeAccount_WithMissingLinks_Json_PrintsTheThreeLists()
    {
        var (_, _, items) = await CreateCollection([(null, "Mod one")], [ModShape.Plain]);
        var renderer = new CapturingRenderer();

        var code = await CollectionInstallReport.ReportMissingLinks(renderer, [(items[0], new Uri("https://www.nexusmods.com/x/mods/1?tab=files"))], json: true);

        code.Should().Be(2);
        var root = JsonDocument.Parse(renderer.Output).RootElement;
        root.GetProperty("installed").GetArrayLength().Should().Be(0);
        root.GetProperty("pending").GetArrayLength().Should().Be(0);
        var link = root.GetProperty("missingLinks").EnumerateArray().Should().ContainSingle().Subject;
        link.GetProperty("name").GetString().Should().Be("Mod one");
        link.GetProperty("uri").GetString().Should().Be("https://www.nexusmods.com/x/mods/1?tab=files");
    }
}
