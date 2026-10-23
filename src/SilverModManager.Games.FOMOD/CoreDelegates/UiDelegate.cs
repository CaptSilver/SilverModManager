using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.GuidedInstallers.ValueObjects;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Threading;
using GroupId = NexusMods.Abstractions.GuidedInstallers.ValueObjects.GroupId;
using Option = NexusMods.Abstractions.GuidedInstallers.Option;
using OptionGroup = NexusMods.Abstractions.GuidedInstallers.OptionGroup;
using OptionGroupType = NexusMods.Abstractions.GuidedInstallers.OptionGroupType;
using OptionType = NexusMods.Abstractions.GuidedInstallers.OptionType;

namespace NexusMods.Games.FOMOD.CoreDelegates;

public sealed class UiDelegates : FomodInstaller.Interface.ui.IUIDelegates, IDisposable
{
    /// <summary>
    /// Created by the executor, this delegate notifies the executor about which options the
    /// user selected.
    /// </summary>
    /// <param name="stepId">
    /// The ID of the step where the user selected the options.
    /// This is most likely going to be the current step.
    /// </param>
    /// <param name="selectedGroupId">The ID of the selected group.</param>
    /// <param name="selectedOptionIds">The IDs of the selected options.</param>
    private delegate void SelectOptions(int stepId, int selectedGroupId, int[] selectedOptionIds);

    private static void DummySelectOptions(int stepId, int selectedGroupId, int[] selectedOptionIds) { }

    /// <summary>
    /// Created by the executor, this delegate notifies the executor that the installer should
    /// proceed to the next or previous step.
    /// </summary>
    /// <param name="forward">Whether to move forward or backwards in the installer.</param>
    /// <param name="currentStepId">The ID of the current step.</param>
    private delegate void ContinueToNextStep(bool forward, int currentStepId);

    private static void DummyContinueToNextStep(bool forward, int currentStepId) { }

    /// <summary>
    /// Created by the executor, this delegate notifies the executor that the installer
    /// should exit.
    /// </summary>
    private delegate void CancelInstaller();

    private static void DummyCancelInstaller() { }

    private readonly ILogger<UiDelegates> _logger;
    private readonly IGuidedInstaller _guidedInstaller;
    private readonly CancellationToken _cancellationToken;

    private SelectOptions _selectOptions = DummySelectOptions;
    private ContinueToNextStep _continueToNextStep = DummyContinueToNextStep;
    private CancelInstaller _cancelInstaller = DummyCancelInstaller;

    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

    /// <summary>
    /// Set while the library applies a selection we passed on; its next <see cref="UpdateState"/> confirms it.
    /// </summary>
    private TaskCompletionSource? _selectionApplied;

    /// <summary>
    /// How long to wait for the library to confirm a selection. It only fails to when its select task threw,
    /// and then the installation has to fail, since continuing would install the options it had before.
    /// </summary>
    internal TimeSpan SelectionConfirmationTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public IReadOnlyDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly>? CurrentFomodArchiveFiles;

    /// <summary>
    /// Why this installation was cancelled from our side, if it was: the guided installer or the
    /// conversion of a step failed, or <c>cancellationToken</c> fired. The executor only reports a
    /// cancellation, so the caller reads the reason from here.
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <param name="cancellationToken">Handed to the guided installer with every step it is asked to answer.</param>
    public UiDelegates(
        ILogger<UiDelegates> logger,
        IGuidedInstaller guidedInstaller,
        CancellationToken cancellationToken = default)
    {
        _logger = logger;
        _guidedInstaller = guidedInstaller;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Cancels the installation because of <paramref name="reason"/>, so the executor ends instead of
    /// waiting for a step answer that will never come. Only the first reason is kept.
    /// </summary>
    public void Fail(Exception reason)
    {
        Failure ??= reason;

        // NOTE: Cancelling before the executor started the dialog has nothing to cancel yet,
        // StartDialog checks for that.
        _cancelInstaller();
        EndDialog();

        // Only after EndDialog: the step answer waiting for a selection wakes up, and must find the
        // library's delegates already gone.
        Interlocked.Exchange(ref _selectionApplied, null)?.TrySetCanceled();
    }

    /// <summary>
    /// Cancels the installation because the install's cancellation token fired.
    /// </summary>
    public void Cancel() => Fail(new OperationCanceledException(_cancellationToken));

    public void StartDialog(
        string? moduleName,
        FomodInstaller.Interface.HeaderImage image,
        Action<int, int, int[]> select,
        Action<bool, int> cont,
        Action cancel)
    {
        _selectOptions = new SelectOptions(select);
        _continueToNextStep = new ContinueToNextStep(cont);
        _cancelInstaller = new CancelInstaller(cancel);

        _guidedInstaller.SetupInstaller(moduleName ?? "FOMOD Installer");

        if (_cancellationToken.IsCancellationRequested) Cancel();
    }

    public void EndDialog()
    {
        _selectOptions = DummySelectOptions;
        _continueToNextStep = DummyContinueToNextStep;
        _cancelInstaller = DummyCancelInstaller;
        CurrentFomodArchiveFiles = null;

        _guidedInstaller.CleanupInstaller();
    }

    public void ReportError(
        string title,
        string message,
        string details)
    {
        _logger.LogError("Reporting an error: {Title}: {Message}\n{Details}", title, message, details);
    }

    public void UpdateState(FomodInstaller.Interface.ui.InstallerStep[] installSteps, int currentStepId)
    {
        // NOTE(erri120): The FOMOD library we're using can send us stupid inputs.
        if (currentStepId < 0 || currentStepId >= installSteps.Length) return;

        // NOTE(erri120): This fuckery is explained further below when we call _selectOptions()
        if (Interlocked.Exchange(ref _selectionApplied, null) is { } selectionApplied)
        {
            selectionApplied.TrySetResult();
            return;
        }

        var groupIdMappings = new List<KeyValuePair<int, GroupId>>();
        var optionIdMappings = new List<KeyValuePair<int, OptionId>>();

        // The answer below makes the library send its next step from another thread, which can happen
        // before this method returns. AskStep has released the semaphore by then, so that step gets in.
        if (AskStep(installSteps, currentStepId, groupIdMappings, optionIdMappings) is not { } request) return;

        request.ContinueWith(async task =>
            {
              try
              {
                // NOTE: A faulted or cancelled request has to end the installation, the executor
                // would wait for the answer forever.
                var result = task.GetAwaiter().GetResult();

                if (result.IsCancelInstallation)
                {
                    Failure ??= new FomodInstallCancelledException("The FOMOD installation was cancelled");
                    _cancelInstaller();

                    // NOTE(erri120): We have to manually call this method.
                    EndDialog();
                }
                else if (result.IsGoToPreviousStep)
                {
                    _continueToNextStep(forward: false, currentStepId);
                }
                else if (result.IsGoToNextStep)
                {
                    var selectedOptions = result.AsT2.SelectedOptions;

                    // NOTE(erri120): This _selectOptions delegate we got from the executor is
                    // fucking weird. It only accepts the selected options from a single group.
                    // Vortex: https://github.com/Nexus-Mods/Vortex/blob/82e8ad3e051ab4ad41df43d11803ee43d399a85f/src/extensions/installer_fomod/views/InstallerDialog.tsx#L450-L461
                    // This is "supposed" to be called whenever the user clicks on a button in the UI.
                    // However, this explicit relationship doesn't work in our case and is generally
                    // pretty stupid. As such, we get the "final" result from the implementation
                    // and push those all at once to the executor.

                    var groupings = selectedOptions.GroupBy(x => x.GroupId);

                    foreach (var grouping in groupings)
                    {
                        var selectedGroupId = groupIdMappings.First(kv => kv.Value == grouping.Key).Key;
                        var selectedOptionIds = grouping
                            .Select(x => optionIdMappings.FirstOrDefault(kv => kv.Value == x.OptionId).Key)
                            .ToArray();

                        // NOTE(erri120): Once again, the FOMOD library we're using is complete ass and expects
                        // to be used in a JavaScript environment. However, this isn't JavaScript this is C#.
                        // When calling _selectOptions, the library spawns a new Task that runs in the background.
                        // This means that after calling _selectOptions, the state hasn't been updated YET.
                        // Inside _selectOptions, the library wants to get the next step, however, if we
                        // call _continueToNextStep, then the next step variable has already been updated.
                        // The library doesn't do any checks to prevent this and can throw an exception
                        // if we're at the last step and continue. In that case, the step variable will be set to -1
                        // and steps[-1] will throw an exception.
                        // As such, we're required to use our own synchronization. Thankfully, we can abuse the stupid
                        // behavior of the library. As explained earlier, the library expects us to call _selectOptions
                        // when the user clicks on a button. The method _selectOptions will call us back, once it has
                        // updated its internal state. This behavior allows us to wait for the library to call us back.
                        // Don't give up after a short wait: on a busy thread pool the library's task can start long
                        // after we asked, and continuing before it ran installs the options it had before. The
                        // callback only fails to come when that task threw, which the long timeout turns into a
                        // failed install, and Fail ends the wait when the installation is cancelled. Awaiting also
                        // frees this thread, which may be the one the library's task was queued on.
                        var selectionApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Volatile.Write(ref _selectionApplied, selectionApplied);

                        _selectOptions(currentStepId, selectedGroupId, selectedOptionIds);
                        try
                        {
                            await selectionApplied.Task.WaitAsync(SelectionConfirmationTimeout, _cancellationToken);
                        }
                        catch (TimeoutException)
                        {
                            throw new TimeoutException($"The FOMOD installer didn't apply the options chosen at step `{installSteps[currentStepId].name}`");
                        }
                    }

                    // NOTE(erri120): We need to explicitly call this, since our implementations
                    // return from RequestMultipleChoices when the user wants to go
                    // to the next step. Once again, the API is ass.
                    _continueToNextStep(forward: true, currentStepId);
                }
              }
              catch (Exception e)
              {
                  Fail(e);
              }
            });
    }

    /// <summary>
    /// Converts the step and asks the guided installer for it. Returns <c>null</c> when the installation
    /// had to fail instead.
    /// </summary>
    private Task<UserChoice>? AskStep(
        FomodInstaller.Interface.ui.InstallerStep[] installSteps,
        int currentStepId,
        List<KeyValuePair<int, GroupId>> groupIdMappings,
        List<KeyValuePair<int, OptionId>> optionIdMappings)
    {
        // NOTE(erri120): The FOMOD library we're using was designed for threading in JavaScript.
        // A semaphore is required because the library can spawn multiple tasks on different threads
        // that will call this method multiple times. This can lead to double-state and it's
        // just a complete mess. This is what you get when you write a .NET library for JavaScript...
        using var waiter = _semaphoreSlim.WaitDisposable(TimeSpan.Zero);
        if (!waiter.HasEntered)
        {
            // Dropping the step would leave the executor waiting for its answer forever.
            Fail(new InvalidOperationException($"The FOMOD installer sent step `{installSteps[currentStepId].name}` while another step was being set up"));
            return null;
        }

        GuidedInstallationStep guidedInstallationStep;
        try
        {
            guidedInstallationStep = ToGuidedInstallationStep(
                installSteps,
                currentStepId,
                groupIdMappings,
                optionIdMappings
            );
        }
        catch (Exception e)
        {
            // NOTE: The library calls us from a task nobody observes, so a throw here would leave the executor waiting.
            Fail(e);
            return null;
        }

        // Progress is computed as currentIndex + 1 /(all steps before the current step + future visible steps + 1).
        // The +1 are to avoid having the bar at 0% when we're at the first step, and to account for the extra finish screen.
        var progress = Percent.Create(
            current: currentStepId + 1,
            maximum: currentStepId + 1 + installSteps.Skip(currentStepId + 1).Count(step => step.visible) + 1
        );

        try
        {
            return _guidedInstaller.RequestUserChoice(guidedInstallationStep, progress, _cancellationToken);
        }
        catch (Exception e)
        {
            Fail(e);
            return null;
        }
    }

    private GuidedInstallationStep ToGuidedInstallationStep(
        IList<FomodInstaller.Interface.ui.InstallerStep> installSteps,
        int currentStepId,
        ICollection<KeyValuePair<int, GroupId>> groupIdMapping,
        ICollection<KeyValuePair<int, OptionId>> optionIdMappings)
    {
        var groups = installSteps[currentStepId].optionalFileGroups;

        var stepGroups = groups.group.Select(group =>
        {
            var groupId = GroupId.From(Guid.NewGuid());
            groupIdMapping.Add(new KeyValuePair<int, GroupId>(group.id, groupId));

            var groupType = ConvertOptionGroupType(group.type);

            return new OptionGroup
            {
                Id = groupId,
                Type = groupType,
                Name = group.name,
                Options = ToOptions(group.options, optionIdMappings, groupType).ToArray(),
            };
        });

        return new GuidedInstallationStep
        {
            Id = StepId.From(Guid.NewGuid()),
            Name = installSteps[currentStepId].name ?? string.Empty,
            Groups = stepGroups.ToArray(),
            HasPreviousStep = currentStepId != 0,
            HasNextStep = currentStepId != installSteps.Count - 1,
        };
    }

    private IEnumerable<Option> ToOptions(
        IReadOnlyCollection<FomodInstaller.Interface.ui.Option> options,
        ICollection<KeyValuePair<int, OptionId>> optionIdMappings,
        OptionGroupType optionGroupType)
    {
        return options.Select(option =>
        {
            var optionId = OptionId.From(Guid.NewGuid());
            optionIdMappings.Add(new KeyValuePair<int, OptionId>(option.id, optionId));

            var optionType = MakeOptionType(option);

            // NOTE(erri120): If the group only contains a single option that is required by the group type,
            // the option type should be set to required. Some authors and FOMOD builders aren't doing this
            // automatically, so we have to change the type to required afterwards.
            if (options.Count == 1 && optionGroupType is OptionGroupType.ExactlyOne or OptionGroupType.AtLeastOne)
            {
                optionType = OptionType.Required;
            }

            return new Option
            {
                Id = optionId,
                Name = option.name,
                Description = string.IsNullOrWhiteSpace(option.description) ? null : option.description,
                Image = CreateOptionImage(option),
                Type = optionType,
            };
        });
    }

    private OptionImage? CreateOptionImage(FomodInstaller.Interface.ui.Option option)
    {
        var image = option.image;
        if (image is null) return null;
        if (Uri.TryCreate(image, UriKind.Absolute, out var uri)) return new OptionImage(uri);

        var path = RelativePath.FromUnsanitizedInput(image);

        if (CurrentFomodArchiveFiles is null) return null;
        if (!CurrentFomodArchiveFiles.TryGetValue(path, out var libraryArchiveEntry))
        {
            _logger.LogWarning("Image at `{Path}` doesn't exist in the archive", path);
            return null;
        }

        return new OptionImage(new OptionImage.ImageStoredFile(libraryArchiveEntry.AsLibraryFile().Hash));
    }

    private static OptionType MakeOptionType(FomodInstaller.Interface.ui.Option option)
    {
        var state = option.type switch
        {
            "Required" => OptionType.Required,
            "NotUsable" => OptionType.Disabled,
            "Recommended" => OptionType.PreSelected,
            _ => OptionType.Available,
        };

        return state;
    }

    private static OptionGroupType ConvertOptionGroupType(string input)
    {
        return input switch
        {
            "SelectAtLeastOne" => OptionGroupType.AtLeastOne,
            "SelectAtMostOne" => OptionGroupType.AtMostOne,
            "SelectExactlyOne" => OptionGroupType.ExactlyOne,
            _ => OptionGroupType.Any,
        };
    }

    public void Dispose()
    {
        _guidedInstaller.Dispose();
        _semaphoreSlim.Dispose();
    }
}
