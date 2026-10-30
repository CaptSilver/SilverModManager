using System.Diagnostics;
using System.Threading.Channels;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;
using Spectre.Console;
using SpectreRender = Spectre.Console.Rendering;
using SpectreText = Spectre.Console.Text;
using SpectreTable = Spectre.Console.Table;
using SpectreTableColumn = Spectre.Console.TableColumn;

namespace SilverModManager.ProxyConsole;

/// <summary>
/// An adapter for rendering <see cref="IRenderable"/>s to the console using Spectre.Console.
/// </summary>
public class SpectreRenderer : IRenderer
{
    private readonly Spectre.Console.IAnsiConsole _console;
    private Channel<IRenderable>? _progressChannel;
    private Task? _progressTask;

    private Channel<IRenderable> ProgressChannel => _progressChannel
        ?? throw new InvalidOperationException("Progress was not started: render a StartProgress first");

    /// <summary>
    /// Wraps the given <see cref="Spectre.Console.IAnsiConsole"/> instance as a <see cref="IRenderer"/>.
    /// </summary>
    /// <param name="console"></param>
    public SpectreRenderer(Spectre.Console.IAnsiConsole console)
    {
        _console = console;
    }

    /// <summary>
    /// Converts the given <see cref="IRenderable"/> to a <see cref="Render.IRenderable"/> that can be
    /// sent to Spectre.Console.
    /// </summary>
    /// <param name="renderable"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    private async ValueTask<SpectreRender.IRenderable> ToSpectreAsync(IRenderable renderable)
    {
        switch (renderable)
        {
            case SilverModManager.Sdk.ProxyConsole.Text text:
                return new UnwrappedText(RenderDefinitions.TextRenderDefinition.Format(text));
            case SilverModManager.Sdk.ProxyConsole.Table table:
                return await ToSpectreAsync(table);
            default:
                throw new NotImplementedException();
        }
    }

    private async ValueTask<SpectreRender.IRenderable> ToSpectreAsync(SilverModManager.Sdk.ProxyConsole.Table table)
    {
        var t = new SpectreTable();
        foreach (var column in table.Columns)
        {
            t.AddColumn(new SpectreTableColumn(await ToCellAsync(column)));
        }

        foreach (var row in table.Rows)
        {
            var convertedRow = new List<SpectreRender.IRenderable>();
            foreach (var cell in row)
            {
                convertedRow.Add(await ToCellAsync(cell));
            }
            t.AddRow(convertedRow.ToArray());
        }
        return t;
    }

    /// <summary>
    /// Table text keeps Spectre's wrapping so a long cell folds inside its column. Only top-level text is
    /// written unwrapped.
    /// </summary>
    private ValueTask<SpectreRender.IRenderable> ToCellAsync(IRenderable cell)
    {
        return cell is SilverModManager.Sdk.ProxyConsole.Text text
            ? ValueTask.FromResult<SpectreRender.IRenderable>(new SpectreText(RenderDefinitions.TextRenderDefinition.Format(text)))
            : ToSpectreAsync(cell);
    }

    /// <inheritdoc />
    public async ValueTask RenderAsync(IRenderable renderable)
    {
        switch (renderable)
        {
            case StartProgress startProgress:
                StartProgress(startProgress);
                break;
            case StopProgress stopProgress:
                await StopProgress(stopProgress);
                break;
            case CreateProgressTask createProgressTask:
                ProgressChannel.Writer.TryWrite(createProgressTask);
                break;
            case DeleteProgressTask deleteProgressTask:
                ProgressChannel.Writer.TryWrite(deleteProgressTask);
                break;
            case UpdateTask updateTask:
                ProgressChannel.Writer.TryWrite(updateTask);
                break;
            default:
                var spectre = await ToSpectreAsync(renderable);
                _console.Write(spectre);
                break;
        }
        
    }

    private async Task StopProgress(StopProgress _)
    {
        ProgressChannel.Writer.TryComplete();
        await (_progressTask ?? throw new InvalidOperationException("Progress was not started: render a StartProgress first"));
    }

    private void StartProgress(StartProgress _)
    {
        Debug.Assert(_progressChannel == null);
        var channel = Channel.CreateUnbounded<IRenderable>();
        _progressChannel = channel;
        _progressTask = _console.Progress()
            .HideCompleted(true)
            .Columns(
                new SpinnerColumn(), 
                new PercentageColumn(), 
                new ProgressBarColumn(), 
                new RemainingTimeColumn(), 
                new TaskDescriptionColumn {Alignment = Justify.Left}
            )
            .StartAsync(context => ProgressTask(context, channel));
    }

    private static async Task ProgressTask(ProgressContext context, Channel<IRenderable> channel)
    {
        var tasks = new Dictionary<Guid, ProgressTask>();
        await foreach (var renderable in channel.Reader.ReadAllAsync())
        {
            switch (renderable)
            {
                case CreateProgressTask createProgressTask:
                    var task = context.AddTask(createProgressTask.Text, maxValue: 1.0);
                    tasks.Add(createProgressTask.TaskId, task);
                    break;
                case UpdateTask updateTask:
                    if (!tasks.TryGetValue(updateTask.TaskId, out task))
                        break;
                    task.Increment(updateTask.IncrementProgressBy);
                    break;
                case DeleteProgressTask deleteProgressTask:
                    if (!tasks.TryGetValue(deleteProgressTask.TaskId, out task))
                        break;
                    task.StopTask();
                    tasks.Remove(deleteProgressTask.TaskId);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync()
    {
        _console.Clear();
        return ValueTask.CompletedTask;
    }

}
