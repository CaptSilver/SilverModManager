using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests;

public class AccountsSectionTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void TheAccountsSectionListsNexusGitHubBothForumsAndSteam()
    {
        var settings = provider.GetRequiredService<ISettingsManager>();

        var rows = settings.Configs.Values
            .SelectMany(config => config.Properties)
            .Where(property => property.Options.Section == Sections.Accounts)
            .ToList();

        rows.Select(row => row.Options.DisplayName).Should().BeEquivalentTo("Nexus Mods", "GitHub", "LoversLab", "VectorPlexus", "Steam (depot files)");
        rows.Should().OnlyContain(row => row.ContainerOptions is ActionButtonContainerOptions, "every account is an action row, not a value to edit");
    }
}
