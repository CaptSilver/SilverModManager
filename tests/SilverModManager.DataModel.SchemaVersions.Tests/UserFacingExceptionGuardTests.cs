using System.Reflection;
using FluentAssertions;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Keeps the split between refusals and crashes honest: an exception whose message is written for the player derives from
/// <see cref="UserFacingException"/> and is shown as a plain dialog, and anything else is reported as a crash with
/// the details in the log only. A new exception type that is neither is a decision nobody made, so it fails here
/// until it is one or the other.
/// </summary>
public class UserFacingExceptionGuardTests
{
    private const string FormatReader = "Reads a file format and fails on bytes that are not that format; the merge planner turns it into a refusal at the point where it knows which path and mods are involved.";
    private const string Loot = "Failure inside the LOOT metadata and sorting port; the sorter and metadata loader translate it into a sort result or diagnostic, so one that escapes is a bug.";
    private const string DeveloperFacing = "Raised and caught inside the code that throws it, or a programming error; its message is not written for the player.";

    /// <summary>
    /// Exception types that deliberately do not derive from <see cref="UserFacingException"/>, by full name, each with
    /// the reason. A subclass of a listed type is covered by its entry.
    /// </summary>
    private static readonly Dictionary<string, string> InternalExceptions = new()
    {
        ["SilverModManager.Games.FromSoft.Formats.AesException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.Bhd5Exception"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.Bnd3Exception"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.Bnd4Exception"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.DcxException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.FmgException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.EsdException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.RegulationException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.ParamException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.KrakenException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Formats.TpfException"] = FormatReader,
        ["SilverModManager.Games.FromSoft.Merge.MergeException"] = "A strategy's refusal to merge one path; the planner collects it into a MergeRefusal and MergeRefusedException is what the player sees.",
        ["SilverModManager.Games.FromSoft.Merge.EsdMergeException"] = "A talk-script merge refusal; handled the same way as MergeException.",
        ["SilverModManager.Backend.FileExtractor.FileExtractionException"] = "An archive that cannot be unpacked; the install job reports the archive by name around it.",
        ["SilverModManager.MnemonicDB.Abstractions.UniqueConstraintException"] = "A database invariant broken by a bug in a writer.",
        ["SilverModManager.Sdk.Hashes.HashCollisionException"] = DeveloperFacing,
        ["SilverModManager.SingleProcess.Exceptions.SingleProcessLockException"] = "Startup finds the lock held and switches to the running instance; it never reaches a dialog.",
        ["SilverModManager.Games.Unity.BepInEx.BepInExLayoutException"] = "Raised by the installer's layout detection and turned into the installer declining the archive.",
        ["SilverModManager.Games.Bethesda.Loot.Database.ConditionParseException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.InvalidPluginNameException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.MessageFormatException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.MetadataLoadException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.MetadataParseException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.MultilingualMessageContentException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Metadata.YamlMergeKeyException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.BuildGroupsGraphException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.CycleInvolvingPluginException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.CyclicInteractionException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.GraphCycleException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.GroupsPathException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.NegativeCycleException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.PathfindingException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.PluginGraphValidationException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.SortingException"] = Loot,
        ["SilverModManager.Games.Bethesda.Loot.Sorting.UndefinedGroupException"] = Loot,
        ["SilverModManager.Abstractions.Library.ThunderstorePackageInDisabledGroupException"] = "The Thunderstore verbs catch it and word the refusal themselves.",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.NeedsIngestException"] = "Declared by the synchronizer API; nothing throws it.",
        ["SilverModManager.App.GarbageCollection.Errors.UnknownFileException"] = "Raised when the collector is asked to count a file it does not know, which its own documentation calls a bug in the core application.",
        ["SilverModManager.DataModel.MissingArchiveException"] = "The file store lacks an archive it should hold; an inconsistent store, not something the player can fix from the message.",
        ["SilverModManager.Games.AdvancedInstaller.Exceptions.MappingAlreadyExistsException"] = "An installer mapped two files to one location; a bug in the installer definition.",
        ["SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException"] = "A BSA/BA2 that cannot be read; the member opener and the Wabbajack install catch it and name the archive around it.",
        ["SilverModManager.Games.Bethesda.LoadOrder.InvalidPluginException"] = "A plugin file that is not a plugin; the plugin header cache catches it.",
        ["SilverModManager.Games.Bethesda.LoadOrder.Ordering.PluginOrderCycleException"] = "The default plugin order has a dependency cycle; the ordering code catches it.",
        ["SilverModManager.Games.CreationEngine.Creations.CreationsRecordException"] = "The Creations recognizer turns it into an unreadable record set so the caller keeps what it already knows.",
        ["SilverModManager.Games.FOMOD.FomodInstallCancelledException"] = "The player closed the FOMOD window; the installer catches it and cancels the install quietly.",
        ["SilverModManager.Games.FOMOD.SavedChoicesDoNotApplyException"] = "Saved choices that do not fit the FOMOD; the installer catches it and falls back to asking.",
        ["SilverModManager.Games.FromSoft.Merge.EsdMerge+Unplaceable"] = "Control flow inside the talk-script merge; caught there and turned into a merge refusal.",
        ["SilverModManager.Networking.GOG.Exceptions.ForbiddenException"] = "Caught by the GOG verbs, which word the failure.",
        ["SilverModManager.Networking.Steam.Exceptions.FailedToGetRequestCode"] = "Caught by the Steam verbs, which word the failure.",
        ["SilverModManager.Networking.Thunderstore.ThunderstoreBlobIntegrityException"] = "A blob whose bytes do not match its content-addressed URL; the index cache catches it and refetches.",
        ["SilverModManager.Networking.Thunderstore.ThunderstoreIndexUnavailableException"] = "The message states the fault (no cached index for a community) and not the remedy, so it is not shown as it is.",
        ["SilverModManager.ProxyConsole.Exceptions.UnexpectedMessageException"] = "A protocol violation between the proxy console and its host; a bug.",
        ["SilverModManager.Secrets.SecretService.SecretServiceException"] = "Internal to the secret-service client; its store turns it into an unavailable result.",
        ["SilverModManager.Secrets.SecretService.SecretServiceStore+UnavailableException"] = "Internal to the secret-service client; its store turns it into an unavailable result.",
        ["SilverModManager.SingleProcess.Exceptions.HeadlessMainRunning"] = "Startup catches it to decide whether to start a main process; it never reaches a dialog.",
        ["SilverModManager.SingleProcess.Exceptions.NoMainProcessStarted"] = "Startup catches it to decide whether to start a main process; it never reaches a dialog.",
        ["SilverModManager.Wabbajack.Acquire.MemberNotFoundException"] = "A directive names a file the library cannot supply; the install job records a skip row naming it.",
        ["SilverModManager.Wabbajack.Acquire.WabbajackCdnDownloadJob+PermanentCdnException"] = "Carries an error that retrying cannot fix out of the retry loop; unwrapped before anyone sees it.",
        ["SilverModManager.Wabbajack.Install.ArchiveMismatchException"] = "A built archive does not hold the bytes its inputs were written with; the message becomes a skip row.",
        ["SilverModManager.Wabbajack.Install.ArchivesMissingException"] = "Carries the plan rows so the caller can offer to skip the missing archives; the message alone does not say how.",
        ["SilverModManager.Wabbajack.Install.InstallModlistJob+DirectiveSkipped"] = "Control flow that turns one directive into a skip row.",
        ["SilverModManager.Wabbajack.Transforms.PatchException"] = "A delta that does not apply; the install turns it into a skip row.",
        ["SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException"] = "A texture the re-encoder cannot handle; the install turns it into a skip row.",
        ["StardewModdingAPI.Toolkit.Serialization.SParseException"] = "Vendored SMAPI toolkit code under extern/, not ours to reparent.",
        ["SilverModManager.Sdk.Hashes.BoundedAtomicUpdateException"] = DeveloperFacing,
    };

    private static IEnumerable<Type> ExceptionTypes()
        => PersistedNameGuards.LoadAppAssemblies()
            .SelectMany(PersistedNameGuards.LoadableTypes)
            .Where(t => t is { IsClass: true } && typeof(Exception).IsAssignableFrom(t));

    private static bool IsDeclaredInternal(Type type)
    {
        for (var t = type; t is not null && t != typeof(Exception); t = t.BaseType)
            if (t.FullName is { } name && InternalExceptions.ContainsKey(name)) return true;
        return false;
    }

    [Fact]
    public void EveryExceptionIsUserFacingOrDeclaredInternal()
    {
        var types = ExceptionTypes().ToArray();
        types.Should().Contain(t => t.Name == "SynchronizerPreflightException",
            "the scan found no exception types, so it is not looking in the right assemblies");

        var undeclared = types.Where(t => !typeof(UserFacingException).IsAssignableFrom(t) && !IsDeclaredInternal(t))
            .Select(t => t.FullName)
            .Order()
            .ToArray();

        // Joined into the message because the empty-collection failure names only the first item.
        string.Join(Environment.NewLine, undeclared).Should().BeEmpty("derive from UserFacingException if the message is for the player, otherwise add the type to InternalExceptions with the reason");
    }

    [Fact]
    public void EveryInternalExceptionStillExistsAndIsNotUserFacing()
    {
        var byName = ExceptionTypes().ToDictionary(t => t.FullName!);

        InternalExceptions.Keys.Where(name => !byName.ContainsKey(name))
            .Should().BeEmpty("a renamed or removed type leaves a stale entry");
        InternalExceptions.Keys.Where(name => byName.TryGetValue(name, out var type) && typeof(UserFacingException).IsAssignableFrom(type))
            .Should().BeEmpty("a type that became user-facing no longer needs an entry");
        InternalExceptions.Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key).Should().BeEmpty("every entry says why");
    }

    [Fact]
    public void NoTypeIsLeftFromTheReplacedErrorSurfaces()
    {
        var names = PersistedNameGuards.LoadAppAssemblies()
            .SelectMany(PersistedNameGuards.LoadableTypes)
            .Select(t => t.Name)
            .ToHashSet();

        names.Should().NotContain("UnhandledExceptionReporter", "ErrorPresenter replaced it");
        names.Should().NotContain("LoadoutApplierRefusal", "ErrorPresenter shows apply refusals now");
    }
}
