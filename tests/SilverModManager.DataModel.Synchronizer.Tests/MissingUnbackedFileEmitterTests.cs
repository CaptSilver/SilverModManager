using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A loadout file that is neither in the file store nor in the game folder cannot be put back by the app, so the
/// health check names it.
/// </summary>
public class MissingUnbackedFileEmitterTests(ITestOutputHelper helper) : AStubbedGameTest<MissingUnbackedFileEmitterTests>(helper)
{
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton(new BackupLimit(Size.KB * 8))
            .AddSingleton<IBackupOverflowPrompt>(_prompt);
    }

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private void WriteExternalFiles(int count, int eachBytes)
    {
        GameDir.CreateDirectory();
        for (var i = 0; i < count; i++)
        {
            var bytes = new byte[eachBytes];
            Array.Fill(bytes, (byte)(i + 1));
            File.WriteAllBytes((GameDir / $"ext{i}.bin").ToString(), bytes);
        }
    }

    private async Task<List<Abstractions.Diagnostics.Diagnostic>> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = new MissingUnbackedFileEmitter(FileStore);
        return await emitter.Diagnose(Loadout.Load(Connection.Db, loadout.LoadoutId), CancellationToken.None).ToListAsync();
    }

    [Fact]
    public async Task AdoptedFileDeletedOutsideTheApp_GetsADiagnosticNamingIt()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        File.Delete((GameDir / "ext0.bin").ToString());
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().ContainSingle().Which.Title.Should().Contain("ext0.bin");
        (GameDir / "ext1.bin").FileExists.Should().BeTrue("the other files are untouched");
    }

    [Fact]
    public async Task AdoptedFilesStillOnDisk_GetNoDiagnostic()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BackedUpFileDeletedOutsideTheApp_GetsNoDiagnostic()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        var loadout = await CreateLoadout();
        File.Delete((GameDir / "ext0.bin").ToString());
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        (await Diagnose(loadout)).Should().BeEmpty();
    }
}
