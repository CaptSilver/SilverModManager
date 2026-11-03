using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<IAdvancedInstallerHandler>(_manualInstaller);
    }

    private async Task<LibraryItem.ReadOnly> DocsOnlyArchive()
    {
        var path = TemporaryFileManager.CreateFolder().Path.Combine("docs-only.zip");
        await using (var stream = path.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "readme.txt", "docs/guide.md" })
            {
                await using var entryStream = zip.CreateEntry(entry).Open();
                await entryStream.WriteAsync(Encoding.UTF8.GetBytes(entry));
            }
        }

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
    public async Task Default_StillFallsBackToTheManualInstaller()
    {
        var loadout = await CreateLoadout();
        var item = await DocsOnlyArchive();

        var act = async () => await LoadoutManager.InstallItem(item, loadout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Found no installer");
        _manualInstaller.Calls.Should().Be(1);
    }
}
