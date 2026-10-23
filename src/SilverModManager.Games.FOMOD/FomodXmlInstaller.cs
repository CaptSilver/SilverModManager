using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FomodInstaller.Interface;
using FomodInstaller.Scripting;
using FomodInstaller.Scripting.XmlScript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.GuidedInstallers;

using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FOMOD.CoreDelegates;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using IFileSystem = NexusMods.Paths.IFileSystem;
using FomodMod = FomodInstaller.Interface.Mod;

namespace SilverModManager.Games.FOMOD;

public class FomodXmlInstaller : ALibraryArchiveInstaller
{
    private readonly ICoreDelegates _delegates;
    private readonly XmlScriptType _scriptType = new();
    private readonly ILogger<FomodXmlInstaller> _logger;
    private readonly GamePath _fomodInstallationPath;
    private readonly IFileSystem _fileSystem;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IFileStore _fileStore;

    /// <summary>
    /// Creates a new instance of <see cref="FomodXmlInstaller"/> given the provided <paramref name="provider"/> and <paramref name="fomodInstallationPath"/>.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fomodInstallationPath"></param>
    /// <returns></returns>
    public static FomodXmlInstaller Create(IServiceProvider provider, GamePath fomodInstallationPath)
    {
        return new FomodXmlInstaller(provider, provider.GetRequiredService<ILogger<FomodXmlInstaller>>(), fomodInstallationPath);
    }

    private FomodXmlInstaller(
        IServiceProvider serviceProvider,
        ILogger<FomodXmlInstaller> logger,
        GamePath fomodInstallationPath) : base(serviceProvider, logger)
    {
        _logger = logger;
        _fomodInstallationPath = fomodInstallationPath;

        _delegates = serviceProvider.GetRequiredService<ICoreDelegates>();
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
    }

    public override async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(libraryArchive, loadoutGroup, transaction, loadout, null, cancellationToken);
    }

    /// <summary>
    /// Only one guided installer window can be open at a time, so the mods of a collection that need one
    /// take turns.
    /// </summary>
    private static readonly SemaphoreSlim InteractiveFallbackGate = new(1, 1);

    /// <summary>
    /// Installs with the saved <paramref name="options"/>. When they don't fit the FOMOD, the user is asked in
    /// the guided installer, with the saved choices selected, as long as the registered guided installer
    /// supports user interaction; otherwise <see cref="SavedChoicesDoNotApplyException"/> is thrown.
    /// </summary>
    /// <param name="modName">Name of the mod in the collection, shown to the user.</param>
    /// <exception cref="FomodInstallCancelledException">The user cancelled the guided installer.</exception>
    public async ValueTask<InstallerResult> ExecuteWithSavedChoicesAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        FomodOption[] options,
        string modName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(libraryArchive, loadoutGroup, transaction, loadout, options, cancellationToken);
        }
        catch (SavedChoicesDoNotApplyException savedChoicesFailure)
        {
            var guidedInstaller = ServiceProvider.GetRequiredService<IGuidedInstaller>();
            if (!guidedInstaller.SupportsUserInteraction)
            {
                guidedInstaller.Dispose();
                throw;
            }

            _logger.LogWarning(savedChoicesFailure, "Saved choices for `{Mod}` don't apply, asking the user", modName);
            var notice = $"This collection's saved choices for {modName} don't match the mod's installer: {savedChoicesFailure.Details}";
            var interactive = new InteractiveFallbackGuidedInstaller(guidedInstaller, options, notice);

            // The failed run wrote nothing to the transaction, so the user's answer can go into the same one.
            try
            {
                await InteractiveFallbackGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Nothing has taken ownership of the installer yet.
                interactive.Dispose();
                throw;
            }

            try
            {
                return await ExecuteCoreAsync(libraryArchive, loadoutGroup, transaction, loadout, interactive, cancellationToken);
            }
            catch (FomodInstallCancelledException)
            {
                throw new FomodInstallCancelledException($"Skipped: the interactive installer was cancelled. {notice}");
            }
            finally
            {
                InteractiveFallbackGate.Release();
            }
        }
    }

    public async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        FomodOption[]? options,
        CancellationToken cancellationToken)
    {
        // Every execution with saved choices answers its steps from its own installer.
        var guidedInstaller = options is null ? null : new PresetGuidedInstaller(options);
        return await ExecuteCoreAsync(libraryArchive, loadoutGroup, transaction, loadout, guidedInstaller, cancellationToken);
    }

    /// <param name="guidedInstaller">
    /// Answers the steps of this execution only, <c>null</c> to use the installer the delegates were created with.
    /// </param>
    private async ValueTask<InstallerResult> ExecuteCoreAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        IGuidedInstaller? guidedInstaller,
        CancellationToken cancellationToken)
    {
        if (!libraryArchive.Children.TryGetFirst(x => x.Path.EndsWith(FomodConstants.XmlConfigRelativePath), out var xmlFile))
        {
            guidedInstaller?.Dispose();
            return new NotSupported(Reason: "Found no FOMOD data in the archive");
        }

        // `foo/bar/baz/fomod/ModuleConfig.xml` -> `foo/bar/baz`
        // `fomod/ModuleConfig.xml` -> empty string
        var fomodPathPrefix = xmlFile.Path.Parent.Parent;
        var pathPrefixDropCount = fomodPathPrefix.Length == 0 ? 0 : fomodPathPrefix.Depth + 1;

        var fomodArchiveFiles = libraryArchive.Children
            .Where(x => x.Path.InFolder(fomodPathPrefix))
            .Select(x => new KeyValuePair<RelativePath, LibraryArchiveFileEntry.ReadOnly>(x.Path.DropFirst(pathPrefixDropCount), x))
            .DistinctBy(kv => kv.Key)
            .ToFrozenDictionary(kv => kv.Key, kv => kv.Value);

        var mod = new FomodMod(
            listModFiles: fomodArchiveFiles.Keys.Select(static x => x.ToString()).ToList(),
            stopPatterns: FomodConstants.StopPattern,
            installScriptPath: xmlFile.Path.DropFirst(pathPrefixDropCount), 
            tempFolderPath: string.Empty,
            scriptType: _scriptType
        );

        // NOTE(erri120): We're loading the script manually, otherwise the FOMOD library will load the script from the file system
        await mod.InitializeWithoutLoadingScript();

        // NOTE(erri120): The FOMOD library calls us, so this is the only way we can pass data along.
        var delegates = _delegates;
        UiDelegates? presetUiDelegates = null;
        if (_delegates is InstallerDelegates installerDelegates)
        {
            if (guidedInstaller is not null)
            {
                // NOTE(halgari) The support for passing in presets to the installer is utterly broken. So we're going to
                // something different: we will create a new guided installer that will simply emit the user choices based on the
                // provided options.
                // The library reads the UI delegates on every callback, so each execution gets its own. Writing them into
                // the installer's shared delegates let installs running at the same time answer each other's steps.
                presetUiDelegates = new UiDelegates(
                    ServiceProvider.GetRequiredService<ILogger<UiDelegates>>(),
                    guidedInstaller,
                    cancellationToken
                )
                {
                    CurrentFomodArchiveFiles = fomodArchiveFiles,
                };
                delegates = installerDelegates.WithUiDelegates(presetUiDelegates);
            }
            else
            {
                installerDelegates.UiDelegates.CurrentFomodArchiveFiles = fomodArchiveFiles;
            }
        }
        else if (guidedInstaller is not null)
        {
            throw new InvalidOperationException($"A per-execution guided installer needs {nameof(InstallerDelegates)}, got `{_delegates.GetType()}`");
        }

        IList<Instruction> instructions;
        using (presetUiDelegates)
        {
            var rawScript = await LoadScript(xmlFile.AsLibraryFile().Hash, cancellationToken);

            var executor = _scriptType.CreateExecutor(mod, delegates);
            var installScript = _scriptType.LoadScript(rawScript, true);
            FixScript(installScript, fomodArchiveFiles, _logger);

            using var cancellationRegistration = presetUiDelegates is null
                ? default
                : cancellationToken.Register(presetUiDelegates.Cancel);

            try
            {
                instructions = await executor.Execute(installScript, "", null);
            }
            catch (Exception) when (presetUiDelegates?.Failure is not null)
            {
                // The executor only knows it was cancelled; the reason is what made us cancel it.
                ExceptionDispatchInfo.Capture(presetUiDelegates.Failure).Throw();
                throw;
            }

            // Cancelling may end the run without the executor throwing; that must not install what was gathered so far.
            if (presetUiDelegates?.Failure is FomodInstallCancelledException cancelled)
                ExceptionDispatchInfo.Capture(cancelled).Throw();
        }

        var errors = instructions.Where(instruction => instruction.type == "error").ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("; ", errors.Select(err => err.source)));

        foreach (var warning in instructions.Where(instruction => instruction.type == "unsupported"))
            _logger.LogWarning("Installer uses unsupported function: {}", warning.source);

        InstructionsToLoadoutItems(transaction, loadout, loadoutGroup,instructions, fomodArchiveFiles, _fomodInstallationPath);
        return new Success();
    }

    private async ValueTask<string> LoadScript(Hash hash, CancellationToken cancellationToken = default)
    {
        await using var stream = await _fileStore.GetFileStream(hash, cancellationToken);
        using var streamReader = new StreamReader(stream);
        return await streamReader.ReadToEndAsync(cancellationToken);
    }

    private static void FixScript(
        IScript script,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        ILogger? logger = null)
    {
        Debug.Assert(script is XmlScript);
        if (script is not XmlScript xmlScript) return;

        FixPaths(fomodArchiveFiles, xmlScript.RequiredInstallFiles);
        FixPaths(fomodArchiveFiles, xmlScript.ConditionallyInstalledFileSets.SelectMany(x => x.Files));

        foreach (var option in xmlScript.InstallSteps.SelectMany(x => x.OptionGroups).SelectMany(x => x.Options))
        {
            option.ImagePath = FixPath(option.ImagePath, fomodArchiveFiles, logger: logger);
            FixPaths(fomodArchiveFiles, option.Files, logger: logger);
        }
    }

    private static void FixPaths(
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        IEnumerable<InstallableFile> installableFiles,
        ILogger? logger = null)
    {
        foreach (var installableFile in installableFiles)
        {
            installableFile.Source = FixPath(installableFile.Source, fomodArchiveFiles, isDirectory: installableFile.IsFolder, logger: logger);
            installableFile.Destination = RelativePath.FromUnsanitizedInput(RemoveRoot(installableFile.Destination));
        }
    }

    private static readonly char[] TrimChars = ['\\', '/'];
    internal static ReadOnlySpan<char> RemoveRoot(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span = input;
        return span.TrimStart(TrimChars);
    }

    internal static string FixPath(
        string? input,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        bool isDirectory = false,
        ILogger? logger = null)
    {
        var span = RemoveRoot(input);
        if (span.IsEmpty) return string.Empty;

        var path = RelativePath.FromUnsanitizedInput(span);
        if (isDirectory || fomodArchiveFiles.ContainsKey(path)) return path.ToString();

        logger?.LogWarning("Didn't find matching archive file for referenced file in FOMOD `{OldPath}` -> `{NewPath}`", input, path);
        return input ?? string.Empty;
    }

    private void InstructionsToLoadoutItems(
        ITransaction transaction,
        LoadoutId loadoutId,
        LoadoutItemGroup.New loadoutGroup,
        IList<Instruction> instructions,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        GamePath gamePath)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.type == "copy")
            {
                ConvertInstructionCopy(transaction, instruction, loadoutGroup, loadoutId, fomodArchiveFiles, gamePath);
            }
            else if (instruction.type == "enableallplugins")
            {
                // NOTE: The library appends this to every script whose files all installed. There are no plugins to
                // enable in a loadout, so it doesn't become a loadout item.
                _logger.LogDebug("FOMOD finished, ignoring instruction `enableallplugins`");
            }
            // TODO: "mkdir" - not sure if we need/want this
            // "iniedit" - only supported by c# script and modscript installers atm
            // "generatefile" - only supported by c# script installers
            // "enableplugin" - supported in the fomod-installer module but doesn't seem to be emitted anywhere
            else
            {
                _logger.LogWarning("Unknown FOMOD instruction type: {Type}", instruction.type);
            }
        }
    }

    private void ConvertInstructionCopy(
        ITransaction transaction,
        Instruction instruction,
        LoadoutItemGroup.New loadoutGroup,
        LoadoutId loadoutId,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        GamePath gamePath)
    {
        var src = RelativePath.FromUnsanitizedInput(instruction.source);
        var dest = RelativePath.FromUnsanitizedInput(instruction.destination);

        if (!fomodArchiveFiles.TryGetValue(src, out var libraryArchiveFile))
        {
            _logger.LogError("Didn't find source file `{Path}` in FOMOD archive", src);
            return;
        }

        _ = new LoadoutFile.New(transaction, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
            {
                LoadoutItem = new LoadoutItem.New(transaction, id)
                {
                    LoadoutId = loadoutId,
                    Name = libraryArchiveFile.Path.FileName,
                    ParentId = loadoutGroup,
                },
                TargetPath = new GamePath(gamePath.LocationId, gamePath.Path.Join(dest)).ToGamePathParentTuple(loadoutId),
            },
            Hash = libraryArchiveFile.AsLibraryFile().Hash,
            Size = libraryArchiveFile.AsLibraryFile().Size,
        };
    }
}
