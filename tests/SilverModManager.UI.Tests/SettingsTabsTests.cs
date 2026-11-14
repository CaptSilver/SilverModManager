using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.UI.Tests;

public class SettingsTabsTests(IServiceProvider provider) : AUiTest(provider)
{
    private SectionDescriptor Section(SectionId id) => Provider.GetServices<SectionDescriptor>().Single(s => s.Id == id);

    [Fact]
    public void EveryRegisteredSectionSitsOnItsTab()
    {
        Section(Sections.General).Tab.Should().Be(SettingsTab.General);
        Section(Sections.Theme).Tab.Should().Be(SettingsTab.Appearance);
        Section(Sections.Accounts).Tab.Should().Be(SettingsTab.Accounts);
        Section(Sections.Advanced).Tab.Should().Be(SettingsTab.Accounts);
        Section(Sections.Advanced).Name.Should().Be("Key storage");
        Section(Sections.DeveloperTools).Tab.Should().Be(SettingsTab.Developer);
        Section(Sections.Experimental).Tab.Should().Be(SettingsTab.Developer);
        Provider.GetServices<SectionDescriptor>().Select(s => s.Name).Should().NotContain(["Privacy", "Game specific"]);
    }

    [Theory]
    [InlineData(SettingsTab.General, "General")]
    [InlineData(SettingsTab.Appearance, "Appearance")]
    [InlineData(SettingsTab.Downloads, "Downloads")]
    [InlineData(SettingsTab.Accounts, "Accounts & API keys")]
    [InlineData(SettingsTab.Developer, "Developer")]
    public void EveryTabHasAHeader(SettingsTab tab, string name)
    {
        var (header, icon) = SettingsTabs.Describe(tab);
        header.Should().Be(name);
        icon.Should().NotBeNull();
    }

    private static SectionDescriptor D(SectionId id, string name, uint priority, SettingsTab tab) =>
        new(id, name, () => IconValues.Desktop, priority, Tab: tab);

    private static IReadOnlyList<TabGroup<(SectionId Section, string Name)>> Group(
        IEnumerable<SectionDescriptor> sections, params (SectionId Section, string Name)[] rows) =>
        SettingsTabs.Group(sections, rows, r => r.Section, r => r.Name);

    [Fact]
    public void TabsComeOutInEnumOrderAndEmptyTabsAreLeftOut()
    {
        var sections = new[]
        {
            D(Sections.DeveloperTools, "Developer tools", 1, SettingsTab.Developer),
            D(Sections.General, "General", 9, SettingsTab.General),
            D(Sections.Accounts, "Accounts", 5, SettingsTab.Accounts),
        };

        var result = Group(sections, (Sections.General, "a"), (Sections.DeveloperTools, "b"));

        result.Select(t => t.Tab).Should().Equal(SettingsTab.General, SettingsTab.Developer);
    }

    [Fact]
    public void SectionsInsideATabComeHighestPriorityFirst()
    {
        var accounts = Group(
            [D(Sections.Advanced, "Key storage", ushort.MinValue + 2, SettingsTab.Accounts), D(Sections.Accounts, "Accounts", ushort.MaxValue - 2, SettingsTab.Accounts)],
            (Sections.Advanced, "a"), (Sections.Accounts, "b"));
        accounts.Single().Sections.Select(s => s.Section.Id).Should().Equal(Sections.Accounts, Sections.Advanced);

        var developer = Group(
            [D(Sections.Experimental, "Experimental", ushort.MinValue, SettingsTab.Developer), D(Sections.DeveloperTools, "Developer tools", ushort.MinValue + 1, SettingsTab.Developer)],
            (Sections.Experimental, "a"), (Sections.DeveloperTools, "b"));
        developer.Single().Sections.Select(s => s.Section.Id).Should().Equal(Sections.DeveloperTools, Sections.Experimental);
    }

    [Fact]
    public void RowsInsideASectionAreSortedByDisplayNameIgnoringCase()
    {
        var result = Group([D(Sections.General, "General", 1, SettingsTab.General)],
            (Sections.General, "zeta"), (Sections.General, "Alpha"), (Sections.General, "beta"));

        result.Single().Sections.Single().Rows.Select(r => r.Name).Should().Equal("Alpha", "beta", "zeta");
    }

    [Fact]
    public void ATabWithOneSectionShowsNoHeaderAndATabWithTwoDoes()
    {
        var sections = new[]
        {
            D(Sections.General, "General", 9, SettingsTab.General),
            D(Sections.Accounts, "Accounts", 5, SettingsTab.Accounts),
            D(Sections.Advanced, "Key storage", 2, SettingsTab.Accounts),
        };

        var result = Group(sections, (Sections.General, "a"), (Sections.Accounts, "b"), (Sections.Advanced, "c"));

        result.Single(t => t.Tab == SettingsTab.General).ShowsSectionHeaders.Should().BeFalse();
        result.Single(t => t.Tab == SettingsTab.Accounts).ShowsSectionHeaders.Should().BeTrue();
    }

    [Fact]
    public void ExperimentalJoinsTheDeveloperTabOnlyWhenItsDescriptorIsPresent()
    {
        var developerTools = D(Sections.DeveloperTools, "Developer tools", 1, SettingsTab.Developer);
        var experimental = D(Sections.Experimental, "Experimental", 0, SettingsTab.Developer);
        var rows = new[] { (Sections.DeveloperTools, "a"), (Sections.Experimental, "b") };

        Group([developerTools, experimental], rows).Single().Sections.Should().HaveCount(2);
        Group([developerTools], rows).Single().Sections.Should().HaveCount(1);
    }

    [Fact]
    public void ARowWhoseSectionHasNoDescriptorIsDropped()
    {
        var result = Group([D(Sections.General, "General", 1, SettingsTab.General)],
            (Sections.General, "kept"), (Sections.Theme, "dropped"));

        result.Single().Sections.Single().Rows.Select(r => r.Name).Should().Equal("kept");
    }

    [Fact]
    public void TheDownloadsTabHoldsTheRowsAboutHowFilesReachTheApp()
    {
        var rows = Provider.GetRequiredService<ISettingsManager>().Configs.Values
            .SelectMany(c => c.Properties)
            .Where(p => p.Options.Section == Sections.Downloads)
            .Select(p => p.Options.DisplayName);
        rows.Should().BeEquivalentTo("Max Parallel Downloads", "Handle nxm:// links", "Handle Thunderstore (ror2mm://) links", "Handle GameBanana (silvermodmanager://) links", "HTTP proxy");
        Section(Sections.Downloads).Tab.Should().Be(SettingsTab.Downloads);
    }
}
