using System.Runtime.CompilerServices;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Diagnostics.Values;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.Generic;
using NexusMods.Games.Larian.BaldursGate3.Utils;
using NexusMods.Games.Larian.BaldursGate3.Utils.LsxXmlParsing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Games.Larian.BaldursGate3.Utils.PakParsing;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using Polly;
using NexusMods.Sdk.Resources;

namespace NexusMods.Games.Larian.BaldursGate3.Emitters;

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
        var pakLoadoutFiles = GetAllPakLoadoutFiles(loadout, onlyEnabledMods: true);
        var metaFileTuples = await GetAllPakMetadata(pakLoadoutFiles,
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
            var loadoutItemGroup = mod.AsLoadoutItemWithTargetPath().AsLoadoutItem().Parent;

            // error case
            if (metadataOrError.Exception is not null)
            {
                diagnostics.Add(Diagnostics.CreateInvalidPakFile(
                        ModName: loadoutItemGroup.ToReference(loadout),
                        PakFileName: mod.AsLoadoutItemWithTargetPath().TargetPath.Item3.FileName
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
                    diagnostics.Add(IsOverridesGroup(loadoutItemGroup)
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
                var matchLoadoutItemGroup = highestInstalledMatch.Item1.AsLoadoutItemWithTargetPath().AsLoadoutItem().Parent;

                // Check if found dependency is outdated
                if (installedMatchModule.SemanticVersion < dependency.SemanticVersion)
                {
                    var outdatedModuleName = pakMetaData.MetaFileData.ModuleShortDesc.Name;
                    var outdatedModuleVersion = pakMetaData.MetaFileData.ModuleShortDesc.SemanticVersion.ToString();
                    diagnostics.Add(IsOverridesGroup(loadoutItemGroup)
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

    private static async IAsyncEnumerable<ValueTuple<LoadoutFile.ReadOnly, Outcome<LspkPackageFormat.PakMetaData>>> GetAllPakMetadata(
        LoadoutFile.ReadOnly[] pakLoadoutFiles,
        IResourceLoader<Hash, Outcome<LspkPackageFormat.PakMetaData>> metadataPipeline,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var pakLoadoutFile in pakLoadoutFiles)
        {
            Resource<Outcome<LspkPackageFormat.PakMetaData>> resource;
            try
            {
                resource = await metadataPipeline.LoadResourceAsync(pakLoadoutFile.Hash, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Exception while getting metadata for `{Name}`", pakLoadoutFile.AsLoadoutItemWithTargetPath().TargetPath.Item3);
                continue;
            }

            // Log the InvalidDataException case, but still return the resource
            if (resource.Data.Exception is not null)
            {
                logger.LogWarning(resource.Data.Exception, "Detected invalid BG3 Pak file: `{Name}`", pakLoadoutFile.AsLoadoutItemWithTargetPath().TargetPath.Item3);
            }

            yield return (pakLoadoutFile, resource.Data);
        }
    }

    private static LoadoutFile.ReadOnly[] GetAllPakLoadoutFiles(
        Loadout.ReadOnly loadout,
        bool onlyEnabledMods)
    {
        return LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .Where(group => !onlyEnabledMods || !group.AsLoadoutItem().IsDisabled)
            // TODO: Quite inefficient way to find pak files, need to add markers on pak LoadoutFiles and mods containing them
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath()
                .OfTypeLoadoutFile()
                .Where(file => file.AsLoadoutItemWithTargetPath().TargetPath.Item2 == Bg3Constants.ModsLocationId &&
                               file.AsLoadoutItemWithTargetPath().TargetPath.Item3.Extension == Bg3Constants.PakFileExtension
                )
            )
            .ToArray();
    }

    private NamedLink NexusModsLink => new("Nexus Mods - Baldur's Gate 3", NexusModsUrlBuilder.GetGameUri(_gameDomain));
    private NamedLink BG3SENexusModsLink => new("Nexus Mods", NexusModsUrlBuilder.GetModUri(_gameDomain, ModId.From(2172)));

#endregion Helpers
}
