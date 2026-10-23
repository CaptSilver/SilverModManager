using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Pages.CollectionDownload;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk;

namespace NexusMods.DataModel.SchemaVersions.Tests;

/// <summary>
/// The <c>$type</c> discriminators in saved window and workspace data are persisted names. Changing one makes
/// the saved JSON undeserialisable, which drops the user's workspaces. A rename of the C# namespaces must leave
/// every discriminator as it is: the strings below were written under today's names and are literals on purpose.
/// </summary>
public class JsonNameCompatibilityTests(JsonSerializerOptions jsonOptions)
{
    /// <summary>
    /// Discriminators that carry no prefix at all. They are stored in saved workspaces exactly like this, so they
    /// are not to be "fixed" into prefixed names, and a new one is a decision that belongs in this table.
    /// </summary>
    private static readonly (string TypeName, string JsonName)[] BareNames =
    [
        ("CollectionDownloadPageContext", "CollectionDownloadPageContext"),
        ("DebugControlsPageContext", "DebugControlsPageContext"),
        ("ObservableInfoPageContext", "ObservableInfoPageContext"),
        ("ProtocolRegistrationTestPageContext", "ProtocolRegistrationTestPageContext"),
        ("TextEditorPageContext", "TextEditorPageContext"),
    ];

    private static (Type Type, string Name)[] AllJsonNames() => PersistedNameGuards.LoadAppAssemblies()
        .SelectMany(PersistedNameGuards.LoadableTypes)
        .SelectMany(t => t.GetCustomAttributes<JsonNameAttribute>(inherit: false).Select(a => (Type: t, a.Name)))
        .ToArray();

    [Fact]
    public void NoDiscriminatorUsesTheUpstreamPrefix()
    {
        var names = AllJsonNames();

        names.Should().NotBeEmpty("the scan found no [JsonName] types, so it is not looking in the right assemblies");
        names.Where(x => x.Name.StartsWith(FirstPartyNames.UpstreamPersistedPrefix, StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName}: {x.Name}")
            .Should().BeEmpty();
    }

    [Fact]
    public void DiscriminatorsAreUnique()
    {
        AllJsonNames().GroupBy(x => x.Name).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty();
    }

    [Fact]
    public void TheUnprefixedDiscriminatorsAreExactlyTheKnownOnes()
    {
        var bare = AllJsonNames()
            .Where(x => !x.Name.Contains('.'))
            .Select(x => (TypeName: x.Type.Name, JsonName: x.Name))
            .OrderBy(x => x.TypeName, StringComparer.Ordinal)
            .ToArray();

        bare.Should().Equal(BareNames.OrderBy(x => x.TypeName, StringComparer.Ordinal));
    }

    [Fact]
    public void SortRulesKeepTheirStoredNames()
    {
        NameOf(typeof(After<,>)).Should().Be("SilverModManager.Abstractions.DataModel.Entities.Sorting.After");
        NameOf(typeof(Before<,>)).Should().Be("SilverModManager.Abstractions.DataModel.Entities.Sorting.Before");
        NameOf(typeof(First<,>)).Should().Be("SilverModManager.Abstractions.DataModel.Entities.Sorting.First");
    }

    [Fact]
    public void WorkspaceContextsWrittenUnderTodaysNamesDeserialise()
    {
        Read<IWorkspaceContext>("""{"$type":"SilverModManager.App.UI.WorkspaceSystem.EmptyContext"}""").Should().BeOfType<EmptyContext>();
        Read<IWorkspaceContext>("""{"$type":"SilverModManager.App.UI.WorkspaceSystem.HomeContext"}""").Should().BeOfType<HomeContext>();
        Read<IWorkspaceContext>("""{"$type":"SilverModManager.App.UI.WorkspaceSystem.DownloadsContext"}""").Should().BeOfType<DownloadsContext>();

        var loadout = Read<IWorkspaceContext>("""{"$type":"SilverModManager.App.UI.WorkspaceSystem.LoadoutContext","LoadoutId":72057594037928002}""")
            .Should().BeOfType<LoadoutContext>().Subject;
        loadout.LoadoutId.Value.Value.Should().Be(72057594037928002UL);
    }

    [Fact]
    public void PageContextsWithUnprefixedNamesDeserialise()
    {
        var download = Read<IPageFactoryContext>("""{"$type":"CollectionDownloadPageContext","CollectionRevisionMetadataId":72057594037928003,"TargetLoadout":72057594037928002}""")
            .Should().BeOfType<CollectionDownloadPageContext>().Subject;
        download.TargetLoadout.Value.Value.Should().Be(72057594037928002UL);
        download.CollectionRevisionMetadataId.Value.Value.Should().Be(72057594037928003UL);

        Read<IPageFactoryContext>("""{"$type":"ProtocolRegistrationTestPageContext"}""").Should().BeOfType<ProtocolRegistrationTestPageContext>();
    }

    [Fact]
    public void PageContextsWithPrefixedNamesDeserialise()
    {
        Read<IPageFactoryContext>("""{"$type":"SilverModManager.App.UI.Pages.SettingsPageContext"}""").Should().BeOfType<NexusMods.App.UI.Pages.Settings.SettingsPageContext>();
    }

    private static string NameOf(Type type) => type.GetCustomAttributes(typeof(JsonNameAttribute), inherit: false).Cast<JsonNameAttribute>().Single().Name;

    private T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, jsonOptions)!;
}
