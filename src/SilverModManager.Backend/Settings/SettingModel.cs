using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Backend;

/// <summary>
/// Settings are stored as a simple key-value pair.
/// </summary>
public partial class Setting : IModelDefinition
{
    private const string Namespace = "SilverModManager.DataModel.Settings";

    /// <summary>
    /// Name of the setting
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name)) {IsIndexed = true};

    /// <summary>
    /// Value of the setting
    /// </summary>
    public static readonly StringAttribute Value = new(Namespace, nameof(Value));
}
