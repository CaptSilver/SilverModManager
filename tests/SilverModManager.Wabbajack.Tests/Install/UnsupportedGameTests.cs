using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.Games.CreationEngine;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Models;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Tests.Acquire;
using NexusMods.Wabbajack.Tests.Fixtures;
using OblivionRemastered = NexusMods.Games.CreationEngine.OblivionRemastered.OblivionRemastered;

namespace NexusMods.Wabbajack.Tests.Install;

public class UnsupportedGameTests(ITestOutputHelper helper) : AIsolatedGameTest<UnsupportedGameTests, OblivionRemastered>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddWabbajack()
        .OverrideSettingsForTests<WabbajackSettings>(settings => settings with
        {
            InboxPath = new ConfigurablePath(KnownPath.EntryDirectory, $"{Startup.SettingsRoot}/Inbox-{Guid.NewGuid()}"),
            ExtrasRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"{Startup.SettingsRoot}/Extras-{Guid.NewGuid()}"),
        })
        .AddFakeToolRunner()
        .AddUniversalGameLocator<OblivionRemastered>(new Version("1.0.0"), locationsFileSystem: IsolatedPrefixFileSystem.Create);

    [Fact]
    public async Task OblivionRemasteredListsAreRefusedWithTheGamesReason()
    {
        var loadout = await CreateLoadout();
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, WabbajackFixture.EmptyList() with { GameType = "OblivionRemastered" });
        var added = await LibraryService.AddLocalFile(path);
        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var modlist).Should().BeTrue();

        var refused = await Record.ExceptionAsync(async () => await InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, modlist, new InstallOptions(false, new HashSet<RelativePath>())));

        refused.Should().BeOfType<UnsupportedModlistException>().Which.Message
            .Should().StartWith("Wabbajack lists for ").And.Contain("not supported yet").And.Contain("Unreal");
    }
}
