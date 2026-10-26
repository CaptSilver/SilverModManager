using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Overlays;

namespace SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;

/// <summary>
/// The dialog that shows what a prefix setup would change and, once the user names the prefix, runs it.
/// </summary>
public interface IPrefixSetupViewModel : IOverlayViewModel<Unit>
{
    /// <summary>The game the prefix belongs to.</summary>
    string GameName { get; }

    /// <summary>The prefix that would be changed, as the runner resolved it.</summary>
    string PrefixPath { get; }

    /// <summary>Where the registry files are copied first, or empty when no step touches the registry.</summary>
    string BackupPath { get; }

    /// <summary>The warning that other uses of the game share this prefix, or empty.</summary>
    string SharedNote { get; }

    /// <summary>One entry per component: what was found and what would be fetched and done.</summary>
    IReadOnlyList<PrefixPreviewStep> Steps { get; }

    /// <summary>The checkbox text, which names <see cref="PrefixPath"/>.</summary>
    string ConfirmationText { get; }

    /// <summary>Whether the user ticked the confirmation. Run needs it.</summary>
    bool IsConfirmed { get; set; }

    /// <summary>True while the runner works.</summary>
    bool IsRunning { get; }

    /// <summary>True once every step ended installed or already installed.</summary>
    bool Succeeded { get; }

    /// <summary>True when the last run ended in a failed step or a refusal; Retry is then offered.</summary>
    bool HasFailed { get; }

    /// <summary>Why the last run did not finish, or empty.</summary>
    string ErrorMessage { get; }

    /// <summary>One line per finished step of the current run.</summary>
    ObservableCollection<string> Progress { get; }

    ReactiveCommand<Unit, Unit> RunCommand { get; }

    /// <summary>Runs the setup again after a failure. Steps that already installed are skipped by detection.</summary>
    ReactiveCommand<Unit, Unit> RetryCommand { get; }

    ReactiveCommand<Unit, Unit> CloseCommand { get; }
}

/// <summary>
/// One component's block in the preview.
/// </summary>
/// <param name="Heading">The component's name.</param>
/// <param name="Lines">What the prefix showed, the downloads with their hashes, and the actions.</param>
public sealed record PrefixPreviewStep(string Heading, IReadOnlyList<string> Lines);
