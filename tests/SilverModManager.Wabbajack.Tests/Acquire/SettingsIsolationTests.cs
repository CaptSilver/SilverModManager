using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// WabbajackSettings is saved as a JSON file under XDG_DATA_HOME. Test hosts used to share the process's folder, so
/// a flag one test left behind, or one a test running in parallel was setting, showed up in another test's host.
/// </summary>
public class SettingsIsolationTests(ITestOutputHelper helper) : AcquireTestBase<SettingsIsolationTests>(helper)
{
    [Fact]
    public void ASettingSavedByOneHostNeverReachesTheProcessWideConfigFolder()
    {
        var marker = LibraryFileId.From(EntityId.From(0x5E77_0000_0000UL + (ulong)Random.Shared.Next(1, int.MaxValue)));

        PendingDownload.Set(ServiceProvider.GetRequiredService<ISettingsManager>(), marker);

        var shared = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.XDG_DATA_HOME)
            .Combine($"{ApplicationConstants.DataDirectoryName}/Configs");
        var leaked = shared.DirectoryExists()
            ? shared.EnumerateFiles().Where(file => File.ReadAllText(file.ToString()).Contains(marker.Value.Value.ToString("X"))).ToArray()
            : [];
        leaked.Should().BeEmpty("each host keeps its own settings files");
    }

    [Fact]
    public void AFreshHostStartsWithoutARestartFlag()
    {
        PendingDownload.Read(ServiceProvider.GetRequiredService<ISettingsManager>()).Should().BeNull();
    }
}
