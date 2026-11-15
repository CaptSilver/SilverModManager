using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// One <see cref="ModUpdateRequest"/> being applied: runs the source's steps, collects what they did and turns each
/// step's failure into a <see cref="ModUpdateFailure"/>. The user's cancel stops new steps and reports nothing.
/// </summary>
/// <param name="request">The batch being applied.</param>
/// <param name="library">
/// Swaps the new items in. Null for a source whose steps swap through something else (Thunderstore's installer),
/// which makes <see cref="Replace"/> throw rather than do nothing.
/// </param>
/// <param name="cancellationToken">The user's cancel.</param>
internal sealed class ModUpdateBatch(ModUpdateRequest request, ILibraryService? library, CancellationToken cancellationToken)
{
    // Nexus runs its steps concurrently, so every collection below is only touched under this.
    private readonly object _gate = new();
    private readonly List<LibraryItem.ReadOnly> _downloaded = [];
    private readonly HashSet<EntityId> _downloadedIds = [];
    private readonly List<ModUpdateReplacement> _replacements = [];
    private readonly List<ModUpdateFailure> _failures = [];
    private readonly List<Uri> _opened = [];
    private bool _accountCannotDownload;

    public ModUpdateRequest Request { get; } = request;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Records a new library item. The same item twice is listed once.
    /// </summary>
    public void Downloaded(LibraryItem.ReadOnly item)
    {
        lock (_gate)
        {
            if (_downloadedIds.Add(item.Id)) _downloaded.Add(item);
        }
    }

    /// <summary>
    /// Records a page the app handed to the browser.
    /// </summary>
    public void Opened(Uri uri)
    {
        lock (_gate) _opened.Add(uri);
    }

    /// <summary>
    /// Records that the source could only have updated through the browser, and was not allowed to open it.
    /// </summary>
    public void AccountCannotDownload()
    {
        lock (_gate) _accountCannotDownload = true;
    }

    /// <summary>
    /// Takes in what a nested apply (Thunderstore's installer) already downloaded and swapped.
    /// </summary>
    public void Absorb(ModUpdateOutcome outcome)
    {
        foreach (var item in outcome.Downloaded) Downloaded(item);

        lock (_gate) _replacements.AddRange(outcome.Replacements);
    }

    /// <summary>
    /// Swaps <paramref name="added"/> in for <paramref name="old"/> in every loadout, keeping the old one's place,
    /// and records how it went.
    /// </summary>
    public async ValueTask Replace(LibraryItem.ReadOnly old, LibraryItem.ReadOnly added)
    {
        if (library is null) throw new InvalidOperationException("This batch was built without a library service to replace with.");

        var result = await library.ReplaceLinkedItemsInAllLoadouts(old, added, ReplaceLibraryItemOptions.ModUpdate);
        lock (_gate) _replacements.Add(new ModUpdateReplacement(old, added, result));
    }

    /// <summary>
    /// Starts the steps in order, at most <paramref name="parallelism"/> at a time, and waits for all of them. Once
    /// the user cancels no further step starts. A step's cancellation while the user's token is cancelled reports
    /// nothing; any other exception, an HTTP timeout's <see cref="TaskCanceledException"/> included, becomes a
    /// failure of that step.
    /// </summary>
    public async ValueTask<ModUpdateOutcome> Run(IEnumerable<UpdateStep> steps, int parallelism = 1)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, parallelism));
        var running = new List<Task>();
        foreach (var step in steps)
        {
            try
            {
                await slots.WaitAsync(CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            running.Add(RunStep(step, slots));
        }

        await Task.WhenAll(running);

        lock (_gate)
            return new ModUpdateOutcome([.. _downloaded], [.. _replacements], [.. _failures], [.. _opened], _accountCannotDownload);
    }

    private async Task RunStep(UpdateStep step, SemaphoreSlim slots)
    {
        try
        {
            // A slot can free up in the same moment the user cancels; the step must not start then.
            if (CancellationToken.IsCancellationRequested) return;

            await step.Apply(this);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            // The user cancelled; there is nothing to report.
        }
        catch (Exception e)
        {
            lock (_gate) _failures.Add(new ModUpdateFailure(step.Update, step.Name, e));
        }
        finally
        {
            slots.Release();
        }
    }
}
