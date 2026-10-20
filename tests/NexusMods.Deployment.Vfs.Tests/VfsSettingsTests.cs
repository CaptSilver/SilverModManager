using System.Text.Json;
using FluentAssertions;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Vfs.Tests;

public class VfsSettingsTests
{
    [Fact]
    public void SettingsIdUsesTheProductNamespace() =>
        VfsSettings.SettingsId.Should().Be("SilverModManager.Deployment.VfsSettings");

    [Fact]
    public void DefaultsPutStagingUnderTheDataDirectoryAndKeepTheCacheOn()
    {
        var settings = VfsSettings.CreateDefault(null!);

        settings.StagingLocation.ToString().Should().EndWith("/DataModel/VfsStaging");
        settings.DisableCache.Should().BeFalse();
        settings.PromotionSizeCapBytes.Should().Be(16UL * 1024 * 1024 * 1024);
    }

    [Fact]
    public void DisablingTheCacheSelectsThePolicyWithNoCaching()
    {
        new VfsSettings { DisableCache = true }.CachePolicy.Should().Be(VfsCachePolicy.Disabled);
        new VfsSettings { DisableCache = false }.CachePolicy.Should().Be(VfsCachePolicy.Default);
    }

    [Fact]
    public void TheDerivedCachePolicyIsNotPersisted()
    {
        var json = JsonSerializer.Serialize(new VfsSettings { DisableCache = true });

        json.Should().Contain(nameof(VfsSettings.DisableCache));
        json.Should().NotContain(nameof(VfsSettings.CachePolicy), "it is worked out from DisableCache, and a stored copy would only go stale");
    }
}
