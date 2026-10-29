using SilverModManager.Sdk.ProxyConsole;
using Rendering = Spectre.Console.Rendering;
using Console = Spectre.Console;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.ProxyConsole.RenderDefinitions;

/// <summary>
/// A definition for rendering <see cref="Abstractions.Implementations.Table"/>s to the console using Spectre.Console.
/// </summary>
public class TableRenderDefinition() : ARenderableDefinition<Table>("2148B17A-D6EE-4ECF-9BB4-E70997DA2365")
{
    /// <inheritdoc />
    protected override async ValueTask<Rendering.IRenderable> ToSpectreAsync(Table table, Func<IRenderable, ValueTask<Rendering.IRenderable>> subConvert)
    {
        var t = new Console.Table();
        foreach (var column in table.Columns)
        {
            t.AddColumn(new Console.TableColumn(await ToCell(column, subConvert)));
        }

        foreach (var row in table.Rows)
        {
            var convertedRow = new List<Rendering.IRenderable>();
            foreach (var cell in row)
            {
                convertedRow.Add(await ToCell(cell, subConvert));
            }
            t.Rows.Add(convertedRow.ToArray());
        }
        return t;
    }

    /// <summary>
    /// Table text keeps Spectre's wrapping so a long cell folds inside its column. Only top-level text is
    /// written unwrapped.
    /// </summary>
    private static ValueTask<Rendering.IRenderable> ToCell(IRenderable cell, Func<IRenderable, ValueTask<Rendering.IRenderable>> subConvert)
    {
        return cell is Text text
            ? ValueTask.FromResult<Rendering.IRenderable>(new Console.Text(TextRenderDefinition.Format(text)))
            : subConvert(cell);
    }
}
