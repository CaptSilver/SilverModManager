using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.DataModel.Tests;

public class CreateLoadoutBaselineTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CreateLoadoutBaselineTests>(helper)
{
    [Fact]
    public async Task CreateLoadout_EnsuresTheLocalBaselineBeforeTheLoadoutExists()
    {
        var hashes = (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();
        hashes.BaselineRequests.Clear();

        await LoadoutManager.CreateLoadout(GameInstallation, "Baseline First");

        var request = hashes.BaselineRequests.Should().ContainSingle().Subject;
        request.Installation.Should().Be(GameInstallation);
        request.LoadoutCount.Should().Be(0, "the first synchronize of a new loadout reads the baseline");
    }
}
