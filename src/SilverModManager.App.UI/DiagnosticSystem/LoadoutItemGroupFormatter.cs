using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.App.UI.DiagnosticSystem;

internal sealed class LoadoutItemGroupFormatter(IConnection conn) : IValueFormatter<LoadoutItemGroupReference>
{

    public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, LoadoutItemGroupReference value)
    {
        // TODO: custom markdown control
        var loadoutItemGroup = LoadoutItemGroup.Load(conn.Db, value.DataId);
        if (loadoutItemGroup.IsValid())
        {
            writer.Write(ref state, $"{loadoutItemGroup.AsLoadoutItem().Name}");
        }
        else
        {
            writer.Write(ref state, $"Invalid LoadoutItemGroup entity: {value.DataId}");
        }
    }
}
