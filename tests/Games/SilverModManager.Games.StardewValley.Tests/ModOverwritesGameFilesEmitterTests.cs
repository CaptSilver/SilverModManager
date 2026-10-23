using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;

using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.StardewValley.Emitters;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.StardewValley.Tests;

[Trait("RequiresNetworking", "True")]
public class ModOverwritesGameFilesEmitterTests : ALoadoutDiagnosticEmitterTest<ModOverwritesGameFilesEmitterTests, StardewValley, ModOverwritesGameFilesEmitter>
{
    public ModOverwritesGameFilesEmitterTests(ITestOutputHelper testOutputHelper) : base(testOutputHelper) { }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddStardewValley()
            .AddUniversalGameLocator<StardewValley>(new Version("1.6.14"))
            .AddSingleton<DummyInstaller>();
    }

    [Fact]
    [Trait("RequiresApiKey", "True")]
    public async Task Test_ModOverwritesGameFiles()
    {
        ApiKeyTestHelper.RequireApiKey();
        var loadout = await CreateLoadout();

        // 3D NPC Houses 1.0 https://www.nexusmods.com/stardewvalley/mods/763?tab=files
        var mod = await InstallModFromNexusMods(loadout, ModId.From(763), FileId.From(2874), installer: ServiceProvider.GetRequiredService<DummyInstaller>());

        var diagnostic = await GetSingleDiagnostic(loadout);
        var modOverwritesGameFilesMessageData = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.ModOverwritesGameFilesMessageData>>(because: "mod overwrites game files").Which.MessageData;

        modOverwritesGameFilesMessageData.GroupName.Should().Be("TileFile");
        modOverwritesGameFilesMessageData.Group.DataId.Should().Be(mod.LoadoutItemGroupId);

        await VerifyDiagnostic(diagnostic);
    }
}

file class DummyInstaller : ALibraryArchiveInstaller
{
    public DummyInstaller(IServiceProvider serviceProvider, ILogger<DummyInstaller> logger) : base(serviceProvider, logger) { }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        foreach (var fileEntry in libraryArchive.Children)
        {
            var to = new GamePath(LocationId.Game, fileEntry.Path);

            _ = new LoadoutFile.New(transaction, out var entityId)
            {
                Hash = fileEntry.AsLibraryFile().Hash,
                Size = fileEntry.AsLibraryFile().Size,
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, entityId)
                {
                    TargetPath = to.ToGamePathParentTuple(loadout.Id),
                    LoadoutItem = new LoadoutItem.New(transaction, entityId)
                    {
                        Name = fileEntry.AsLibraryFile().FileName,
                        LoadoutId = loadout,
                        ParentId = loadoutGroup,
                    },
                },
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
