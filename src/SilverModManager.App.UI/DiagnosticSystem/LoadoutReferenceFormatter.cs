using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.DiagnosticSystem;

internal sealed class LoadoutReferenceFormatter(IConnection conn) : IValueFormatter<LoadoutReference>
{
    public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, LoadoutReference value)
    {
        // TODO: custom markdown control
        var loadout = Loadout.Load(conn.Db, value.DataId);
        if (loadout.IsValid())
        {
            writer.Write(ref state, state.Mode == DiagnosticWriterMode.Markdown ? MarkdownText.Inline(loadout.Name) : loadout.Name);
        }
        else
        {
            writer.Write(ref state, $"Invalid Loadout entity: {value.DataId}");
        }
    }
}
