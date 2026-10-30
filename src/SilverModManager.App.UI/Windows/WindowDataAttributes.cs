
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.App.UI.Windows;

public partial class WindowDataAttributes : IModelDefinition
{
    private const string Namespace = "SilverModManager.App.UI.Windows.WindowData";
    
    /// <summary>
    /// The state of the window, stored as json
    /// </summary>
    public static readonly StringAttribute Data = new(Namespace, nameof(Data));
    
    /// <summary>
    /// Encode the window data to a string
    /// </summary>
    public static string Encode(IDb db, WindowData windowData)
    {
        var options = db.Connection.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
        return JsonSerializer.Serialize(windowData, options);
    }

    /// <summary>
    /// Decodes saved window data, dropping what cannot be read instead of failing the whole state: a tab whose
    /// page context no longer deserializes, then a panel, then a workspace. Each drop is logged at Warning.
    /// Throws when the top level itself is unreadable.
    /// </summary>
    public static WindowData Decode(string json, JsonSerializerOptions options, ILogger logger)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new InvalidDataException("The stored window data is not a JSON object");

        if (root[nameof(WindowData.Workspaces)] is not JsonArray workspaces)
            throw new InvalidDataException("The stored window data has no workspaces list");

        foreach (var workspace in workspaces.OfType<JsonObject>())
        {
            if (workspace[nameof(WorkspaceData.Panels)] is not JsonArray panels) continue;

            foreach (var panel in panels.OfType<JsonObject>())
            {
                if (panel[nameof(PanelData.Tabs)] is JsonArray tabs)
                    DropUnreadable<TabData>(tabs, options, logger, "tab", TabDescription);
            }

            DropUnreadable<PanelData>(panels, options, logger, "panel", _ => "");
        }

        DropUnreadable<WorkspaceData>(workspaces, options, logger, "workspace", WorkspaceDescription);

        return root.Deserialize<WindowData>(options)
               ?? throw new InvalidDataException("The stored window data is the JSON literal null");
    }

    private static string TabDescription(JsonNode? tab)
        => $" with page context {ContextType(tab?[nameof(TabData.PageData)]?[nameof(PageData.Context)])}";

    private static string WorkspaceDescription(JsonNode? workspace)
        => $" with context {ContextType(workspace?[nameof(WorkspaceData.Context)])}";

    private static string ContextType(JsonNode? context)
        => context is JsonObject obj && obj["$type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : "(unknown type)";

    private static void DropUnreadable<T>(JsonArray array, JsonSerializerOptions options, ILogger logger, string kind, Func<JsonNode?, string> describe)
    {
        for (var i = array.Count - 1; i >= 0; i--)
        {
            var item = array[i];
            try
            {
                _ = item.Deserialize<T>(options) ?? throw new JsonException("The entry is the JSON literal null");
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Skipping a saved {Kind}{Description} that cannot be read", kind, describe(item));
                array.RemoveAt(i);
            }
        }
    }

    public partial struct ReadOnly
    {
        /// <summary>
        /// The saved window data, minus any tab, panel or workspace that can no longer be read.
        /// </summary>
        public WindowData ReadWindowData(ILogger logger)
            => Decode(Data, Db.Connection.ServiceProvider.GetRequiredService<JsonSerializerOptions>(), logger);
    }
}
