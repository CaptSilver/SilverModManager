
using System.Text.Json;
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

    public partial struct ReadOnly
    {
        public WindowData WindowData
        {
            get
            {
                var options = Db.Connection.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
                return JsonSerializer.Deserialize<WindowData>(Data, options)
                       ?? throw new InvalidDataException("The stored window data is the JSON literal null");
            }
        }
    }
}
