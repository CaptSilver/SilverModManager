using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Pages.About;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests;

public class WindowDataDecodeTests(IServiceProvider provider)
{
    private const string GoneType = "SilverModManager.App.UI.Pages.Gone.RemovedPageContext";

    private JsonSerializerOptions Options => provider.GetRequiredService<JsonSerializerOptions>();

    private static TabData Tab(IPageFactoryContext context) => new()
    {
        Id = PanelTabId.From(Guid.NewGuid()),
        PageData = new PageData { FactoryId = PageFactoryId.From(Guid.NewGuid()), Context = context },
    };

    private static WindowData Window(params TabData[][] panels) => new()
    {
        ActiveWorkspaceId = null,
        Workspaces =
        [
            new WorkspaceData
            {
                Id = WorkspaceId.From(Guid.NewGuid()),
                Context = new HomeContext(),
                Panels = panels.Select(tabs => new PanelData
                {
                    LogicalBounds = new Rect(0, 0, 1, 1),
                    Tabs = tabs,
                    SelectedTabId = PanelTabId.DefaultValue,
                }).ToArray(),
            },
        ],
    };

    private string Encode(WindowData data, Action<JsonObject>? tamper = null)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(data, Options))!.AsObject();
        tamper?.Invoke(node);
        return node.ToJsonString();
    }

    private static JsonObject TabNode(JsonObject root, int panel, int tab)
        => root["Workspaces"]![0]!["Panels"]![panel]!["Tabs"]![tab]!.AsObject();

    [Fact]
    public void ReadableStateDecodesUnchanged()
    {
        var data = Window([Tab(new AboutPageContext()), Tab(new SettingsPageContext())]);
        var logger = new CapturingLogger();

        var decoded = WindowDataAttributes.Decode(Encode(data), Options, logger);

        decoded.Workspaces.Should().ContainSingle().Which.Panels.Should().ContainSingle()
            .Which.Tabs.Should().HaveCount(2);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void ATabWithAnUnreadablePageIsSkippedAndTheOthersKept()
    {
        var wabbajack = Tab(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(7) });
        var data = Window([Tab(new AboutPageContext()), Tab(new SettingsPageContext()), wabbajack]);
        var logger = new CapturingLogger();

        var json = Encode(data, root => TabNode(root, 0, 1)["PageData"]!["Context"]!["$type"] = GoneType);
        var decoded = WindowDataAttributes.Decode(json, Options, logger);

        var tabs = decoded.Workspaces.Single().Panels.Single().Tabs;
        tabs.Select(t => t.PageData.Context.GetType()).Should()
            .Equal(typeof(AboutPageContext), typeof(WabbajackModlistPageContext));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains(GoneType));
    }

    [Fact]
    public void APanelThatCannotBeReadIsSkippedAndTheOtherPanelsKept()
    {
        var data = Window([Tab(new AboutPageContext())], [Tab(new SettingsPageContext())]);
        var logger = new CapturingLogger();

        var json = Encode(data, root => root["Workspaces"]![0]!["Panels"]![0]!["LogicalBounds"] = "not a rect");
        var decoded = WindowDataAttributes.Decode(json, Options, logger);

        decoded.Workspaces.Single().Panels.Should().ContainSingle()
            .Which.Tabs.Single().PageData.Context.Should().BeOfType<SettingsPageContext>();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void AWorkspaceThatCannotBeReadIsSkippedAndTheOtherWorkspacesKept()
    {
        var good = Window([Tab(new AboutPageContext())]).Workspaces[0];
        var bad = Window([Tab(new AboutPageContext())]).Workspaces[0];
        var data = new WindowData { ActiveWorkspaceId = good.Id, Workspaces = [bad, good] };
        var logger = new CapturingLogger();

        var json = Encode(data, root => root["Workspaces"]![0]!["Context"]!["$type"] = GoneType);
        var decoded = WindowDataAttributes.Decode(json, Options, logger);

        decoded.Workspaces.Should().ContainSingle().Which.Id.Should().Be(good.Id);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(GoneType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Workspaces\": 4}")]
    public void UnreadableTopLevelDataThrows(string json)
    {
        var decode = () => WindowDataAttributes.Decode(json, Options, new CapturingLogger());

        decode.Should().Throw<Exception>();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
