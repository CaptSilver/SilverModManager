using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Games.TestFramework;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Vortex drops a <c>__folder_managed_by_vortex</c> marker into every folder it deploys to, so a mod folder carrying
/// one is routine and must not warn. A mod file on any other protected path still does.
/// </summary>
public class ForeignRecordSkipLogTests(ITestOutputHelper helper) : AStubbedGameTest<ForeignRecordSkipLogTests>(helper)
{
    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => _logs.AddTo(base.AddServices(services));

    private async Task SyncModWith(params string[] paths)
    {
        var loadout = await CreateLoadout();
        var files = paths.ToDictionary(path => (RelativePath)path, path => "content"u8.ToArray());
        await using var archiveFile = await CreateTestArchive(files);
        var archive = await RegisterLocalArchive(archiveFile.Path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        await Synchronizer.Synchronize(Refresh(loadout));
    }

    private IEnumerable<CapturedLine> SkipLines(string path) => _logs.Lines.Where(l => l.Message.StartsWith("Skipping") && l.Message.Contains(path));

    [Fact]
    public async Task AVortexMarkerInAModIsSkippedWithoutAWarning()
    {
        await SyncModWith("Data/__folder_managed_by_vortex", "Data/real.txt");

        var lines = SkipLines("__folder_managed_by_vortex").ToArray();
        lines.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task AVortexBackupInAModStillWarns()
    {
        await SyncModWith("Data/original.esp.vortex_backup", "Data/real.txt");

        SkipLines("original.esp.vortex_backup").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task AModFileOnAProtectedPathStillWarns()
    {
        await SyncModWith("saves/mod.sav");

        SkipLines("saves/mod.sav").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }
}
