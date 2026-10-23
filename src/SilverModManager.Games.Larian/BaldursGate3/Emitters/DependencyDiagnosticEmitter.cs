using System.Runtime.CompilerServices;
using System.Xml;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Larian.BaldursGate3.Utils;
using SilverModManager.Games.Larian.BaldursGate3.Utils.LsxXmlParsing;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Games.Larian.BaldursGate3.Utils.PakParsing;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Polly;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.Games.Larian.BaldursGate3.Emitters;

public class DependencyDiagnosticEmitter : ILoadoutDiagnosticEmitter
{
    private readonly ILogger _logger;
    private readonly IResourceLoader<Hash, Outcome<LspkPackageFormat.PakMetaData>> _metadataPipeline;
    private readonly GameDomain _gameDomain;
    private readonly GameModuleCatalog _gameModules;
    private int _loggedUnreadableGame;

    public DependencyDiagnosticEmitter(IServiceProvider serviceProvider, ILogger<DependencyDiagnosticEmitter> logger, IGameDomainToGameIdMappingCache mappingCache, GameModuleCatalog gameModules)
    {
        _logger = logger;
        _gameModules = gameModules;
        _metadataPipeline = Pipelines.GetMetadataPipeline(serviceProvider);
        _gameDomain = mappingCache[BaldursGate3.NexusModsGameId.Value];
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bg3LoadoutFile = ScriptExtenderFile.Find(loadout);

        // The script extender's DWrite.dll only loads under Wine when it is overridden; the diagnostic clears once the prefix has it.
        var dllOverrideInstructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(
            loadout.InstallationInstance,
            WineDllOverrideRequirements.For(loadout.InstallationInstance.Game, loadout),
            GameLauncherSkip.For(loadout.InstallationInstance.Game),
            cancellationToken
        );
        if (dllOverrideInstructions is not null)
        {
            yield return Diagnostics.CreateBg3SeWineDllOverride(Instructions: dllOverrideInstructions)
                with { Actions = WineDiagnosticHelper.GetWineDllOverridesFixActions(loadout.InstallationInstance) };
        }

        var diagnostics = await DiagnosePakModulesAsync(loadout, bg3LoadoutFile, cancellationToken);
        foreach (var diagnostic in diagnostics)
        {
            yield return diagnostic;
        }
    }

#region Diagnosers

    private async Task<IEnumerable<Diagnostic>> DiagnosePakModulesAsync(Loadout.ReadOnly loadout, Optional<LoadoutFile.ReadOnly> bg3LoadoutFile, CancellationToken cancellationToken)
    {
        var paks = GetAllPaks(loadout, onlyEnabledMods: true);
        var metaFileTuples = await GetAllPakMetadata(paks,
                loadout,
                _metadataPipeline,
                _logger,
                cancellationToken
            )
            .ToArrayAsync(cancellationToken: cancellationToken);

        var diagnostics = new List<Diagnostic>();
        var gameModules = GetGameModules(loadout);
        var overridesGroupId = LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id)
            .Select(group => group.Id.Value)
            .FirstOrDefault();

        // The Overrides group's name says nothing about a pak in it, so those diagnostics name the pak module instead.
        bool IsOverridesGroup(LoadoutItemGroup.ReadOnly group) => group.Id.Value == overridesGroupId;

        foreach (var metaFileTuple in metaFileTuples)
        {
            var (mod, metadataOrError) = metaFileTuple;
            var loadoutItemGroup = mod.Group;
            var gameInstalled = mod.Owner == PakOwner.GameInstalled;

            // error case
            if (metadataOrError.Exception is not null)
            {
                diagnostics.Add(Diagnostics.CreateInvalidPakFile(
                        ModName: loadoutItemGroup.ToReference(loadout),
                        PakFileName: mod.FileName
                    )
                );
                continue;
            }

            // non error case
            var pakMetaData = metadataOrError.Result;

            // check if the mod requires the script extender and if SE is missing
            if (pakMetaData.ScriptExtenderConfigMetadata is { HasValue: true, Value.RequiresScriptExtender: true } &&
                !bg3LoadoutFile.HasValue)
            {
                diagnostics.Add(Diagnostics.CreateMissingRequiredScriptExtender(
                    ModName: loadoutItemGroup.ToReference(loadout),
                    PakName:pakMetaData.MetaFileData.ModuleShortDesc.Name,
                    BG3SENexusLink:BG3SENexusModsLink));
            }

            // check dependencies
            var dependencies = pakMetaData.MetaFileData.Dependencies;
            foreach (var dependency in dependencies)
            {
                var dependencyUuid = dependency.Uuid;
                if (dependencyUuid == string.Empty)
                    continue;

                // Modules the game itself ships (GustavX, PhotoMode, ...) are not in the loadout.
                LsxXmlFormat.ModuleShortDesc gameModule = default;
                var providedByGame = gameModules?.TryGetValue(dependencyUuid, out gameModule) ?? false;
                if (gameModules is null && LsxXmlFormat.DependencyUuidsToIgnore.Contains(dependencyUuid, StringComparer.OrdinalIgnoreCase))
                    continue;

                var requiredVersion = dependency.SemanticVersion;
                if (providedByGame && (requiredVersion == default(LsxXmlFormat.ModuleVersion) || gameModule.SemanticVersion >= requiredVersion))
                    continue;

                var matchingDeps = metaFileTuples.Where(
                        x =>
                            x.Item2.Exception is null &&
                            x.Item2.Result.MetaFileData.ModuleShortDesc.Uuid == dependencyUuid
                    )
                    .ToArray();

                if (matchingDeps.Length == 0 && providedByGame)
                {
                    diagnostics.Add(Diagnostics.CreateOutdatedGameModule(
                            ModName: loadoutItemGroup.ToReference(loadout),
                            PakModuleName: pakMetaData.MetaFileData.ModuleShortDesc.Name,
                            PakModuleVersion: pakMetaData.MetaFileData.ModuleShortDesc.SemanticVersion.ToString(),
                            DepName: dependency.Name,
                            MinDepVersion: requiredVersion.ToString(),
                            CurrentDepVersion: gameModule.SemanticVersion.ToString()
                        )
                    );
                    continue;
                }

                if (matchingDeps.Length == 0)
                {
                    // Missing dependency
                    var missingName = dependency.Name;
                    var missingVersion = dependency.SemanticVersion.ToString();
                    var moduleName = pakMetaData.MetaFileData.ModuleShortDesc.Name;
                    var moduleVersion = pakMetaData.MetaFileData.ModuleShortDesc.SemanticVersion.ToString();
                    diagnostics.Add(gameInstalled
                        ? Diagnostics.CreateMissingDependencyInGameInstalled(
                            MissingDepName: missingName,
                            MissingDepVersion: missingVersion,
                            PakModuleName: moduleName,
                            PakModuleVersion: moduleVersion,
                            NexusModsLink: NexusModsLink
                        )
                        : IsOverridesGroup(loadoutItemGroup)
                        ? Diagnostics.CreateMissingDependencyInOverrides(
                            ModName: loadoutItemGroup.ToReference(loadout),
                            MissingDepName: missingName,
                            MissingDepVersion: missingVersion,
                            PakModuleName: moduleName,
                            PakModuleVersion: moduleVersion,
                            NexusModsLink: NexusModsLink
                        )
                        : Diagnostics.CreateMissingDependency(
                            ModName: loadoutItemGroup.ToReference(loadout),
                            MissingDepName: missingName,
                            MissingDepVersion: missingVersion,
                            PakModuleName: moduleName,
                            PakModuleVersion: moduleVersion,
                            NexusModsLink: NexusModsLink
                        )
                    );
                    continue;
                }

                if (dependency.SemanticVersion == default(LsxXmlFormat.ModuleVersion))
                    continue;

                var highestInstalledMatch = matchingDeps.MaxBy(
                    x => x.Item2.Result.MetaFileData.ModuleShortDesc.SemanticVersion
                );
                var installedMatchModule = highestInstalledMatch.Item2.Result.MetaFileData.ModuleShortDesc;
                var matchLoadoutItemGroup = highestInstalledMatch.Item1.Group;

                // Check if found dependency is outdated
                if (installedMatchModule.SemanticVersion < dependency.SemanticVersion)
                {
                    var outdatedModuleName = pakMetaData.MetaFileData.ModuleShortDesc.Name;
                    var outdatedModuleVersion = pakMetaData.MetaFileData.ModuleShortDesc.SemanticVersion.ToString();
                    diagnostics.Add(gameInstalled
                        ? Diagnostics.CreateOutdatedDependencyInGameInstalled(
                            PakModuleName: outdatedModuleName,
                            PakModuleVersion: outdatedModuleVersion,
                            DepName: installedMatchModule.Name,
                            MinDepVersion: dependency.SemanticVersion.ToString(),
                            CurrentDepVersion: installedMatchModule.SemanticVersion.ToString()
                        )
                        : IsOverridesGroup(loadoutItemGroup)
                        ? Diagnostics.CreateOutdatedDependencyInOverrides(
                            ModName: loadoutItemGroup.ToReference(loadout),
                            PakModuleName: outdatedModuleName,
                            PakModuleVersion: outdatedModuleVersion,
                            DepModName: matchLoadoutItemGroup.ToReference(loadout),
                            DepName: installedMatchModule.Name,
                            MinDepVersion: dependency.SemanticVersion.ToString(),
                            CurrentDepVersion: installedMatchModule.SemanticVersion.ToString()
                        )
                        : Diagnostics.CreateOutdatedDependency(
                            ModName: loadoutItemGroup.ToReference(loadout),
                            PakModuleName: outdatedModuleName,
                            PakModuleVersion: outdatedModuleVersion,
                            DepModName: matchLoadoutItemGroup.ToReference(loadout),
                            DepName: installedMatchModule.Name,
                            MinDepVersion: dependency.SemanticVersion.ToString(),
                            CurrentDepVersion: installedMatchModule.SemanticVersion.ToString()
                        )
                    );
                }
            }
        }

        return diagnostics;
    }

#endregion Diagnosers

#region Helpers

    /// <summary>
    /// The modules the installed game provides, or null when its Data folder cannot be read, in which case the
    /// built-in list of known game modules stands in. Logged once, not on every refresh.
    /// </summary>
    private IReadOnlyDictionary<string, LsxXmlFormat.ModuleShortDesc>? GetGameModules(Loadout.ReadOnly loadout)
    {
        var dataFolder = loadout.InstallationInstance.Locations[LocationId.Game].Path.Combine("Data");
        var modules = _gameModules.GetModules(dataFolder);
        if (modules is null && Interlocked.Exchange(ref _loggedUnreadableGame, 1) == 0)
        {
            _logger.LogWarning("Unable to read the game's modules from `{DataFolder}`, using the list of known game modules instead", dataFolder);
        }
        return modules;
    }

    private enum PakOwner { Mod, GameInstalled }

    /// <summary>A pak of the loadout: the group that owns it, and where its bytes come from.</summary>
    private sealed record PakEntry(LoadoutItemGroup.ReadOnly Group, PakOwner Owner, GamePath Path, Hash Hash)
    {
        public RelativePath FileName => Path.FileName;
    }

    private static async IAsyncEnumerable<ValueTuple<PakEntry, Outcome<LspkPackageFormat.PakMetaData>>> GetAllPakMetadata(
        PakEntry[] paks,
        Loadout.ReadOnly loadout,
        IResourceLoader<Hash, Outcome<LspkPackageFormat.PakMetaData>> metadataPipeline,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var pak in paks)
        {
            Outcome<LspkPackageFormat.PakMetaData> outcome;
            if (pak.Owner == PakOwner.GameInstalled)
            {
                // The game's paks are never backed up, so the file store has no copy to read.
                try
                {
                    outcome = ReadFromDisk(loadout, pak, logger);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Exception while getting metadata for `{Name}`", pak.Path);
                    continue;
                }
            }
            else
            {
                Resource<Outcome<LspkPackageFormat.PakMetaData>> resource;
                try
                {
                    resource = await metadataPipeline.LoadResourceAsync(pak.Hash, cancellationToken);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Exception while getting metadata for `{Name}`", pak.Path);
                    continue;
                }

                // Log the InvalidDataException case, but still return the resource
                if (resource.Data.Exception is not null)
                {
                    logger.LogWarning(resource.Data.Exception, "Detected invalid BG3 Pak file: `{Name}`", pak.Path);
                }

                outcome = resource.Data;
            }

            yield return (pak, outcome);
        }
    }

    private static Outcome<LspkPackageFormat.PakMetaData> ReadFromDisk(Loadout.ReadOnly loadout, PakEntry pak, ILogger logger)
    {
        try
        {
            using var stream = loadout.InstallationInstance.Locations.ToAbsolutePath(pak.Path)
                .Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            return Outcome.FromResult(PakFileParser.ParsePakMeta(stream, keepGameModuleDependencies: true));
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Unable to read the BG3 Pak file the game installed: `{Name}`", pak.Path);
            return Outcome.FromException<LspkPackageFormat.PakMetaData>(e);
        }
    }

    private static PakEntry[] GetAllPaks(Loadout.ReadOnly loadout, bool onlyEnabledMods)
    {
        static bool IsPak(GamePath path) => path.LocationId == Bg3Constants.ModsLocationId && path.Extension == Bg3Constants.PakFileExtension;

        var modPaks = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .Where(group => !onlyEnabledMods || !group.AsLoadoutItem().IsDisabled)
            // TODO: Quite inefficient way to find pak files, need to add markers on pak LoadoutFiles and mods containing them
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath()
                .OfTypeLoadoutFile()
                .Where(file => IsPak(file.AsLoadoutItemWithTargetPath().TargetPath))
                .Select(file => new PakEntry(
                    file.AsLoadoutItemWithTargetPath().AsLoadoutItem().Parent,
                    PakOwner.Mod,
                    file.AsLoadoutItemWithTargetPath().TargetPath,
                    file.Hash
                ))
            );

        // The game's paks sit under one group; switching that group off takes them out of the check like any mod.
        var gameInstalledPaks = GameInstalledGroup.FindByInstalledFor(loadout.Db, loadout.Id)
            .Where(group => !onlyEnabledMods || !group.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            .SelectMany(item => item.AsLoadoutItemGroup().Children.OfTypeGameInstalledFile()
                .Where(file => IsPak(file.Path))
                .Select(file => new PakEntry(item.AsLoadoutItemGroup(), PakOwner.GameInstalled, file.Path, file.Hash))
            );

        return modPaks.Concat(gameInstalledPaks).ToArray();
    }

    private NamedLink NexusModsLink => new("Nexus Mods - Baldur's Gate 3", NexusModsUrlBuilder.GetGameUri(_gameDomain));
    private NamedLink BG3SENexusModsLink => new("Nexus Mods", NexusModsUrlBuilder.GetModUri(_gameDomain, ModId.From(2172)));

#endregion Helpers
}
