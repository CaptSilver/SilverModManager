using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Games.CreationEngine;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Tests.Acquire;
using SilverModManager.Wabbajack.Tests.Fixtures;
using OblivionRemastered = SilverModManager.Games.CreationEngine.OblivionRemastered.OblivionRemastered;

namespace SilverModManager.Wabbajack.Tests.Install;

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
