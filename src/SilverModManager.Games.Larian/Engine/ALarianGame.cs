using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Games.Larian.Engine.Installers;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>
/// A game that keeps its mods as LSPK paks and lists them in <c>modsettings.lsx</c>. A game supplies a
/// <see cref="LarianGameProfile"/>; the synchronizer, installers, load order and diagnostics all read it.
/// </summary>
public abstract class ALarianGame<TSelf>(IServiceProvider provider) : AGame<TSelf>(provider), ILarianGame, IWineDllOverrideRequirements
    where TSelf : ALarianGame<TSelf>, IGameData<TSelf>
{
    private LarianGameProfile? _profileValue;
    // One instance, so the synchronizer and the diagnostic read each pak from disk once between them. Built on first
    // use, which can come from either of them on different threads, so it must not be built twice.
    private readonly Lazy<LarianPakModules> _modules = new(() => new LarianPakModules(provider));

    protected abstract LarianGameProfile CreateProfile();

    // Built on first read: CreateProfile is virtual and this constructor runs before a derived one.
    public LarianGameProfile Profile => _profileValue ??= CreateProfile();

    private LarianPakModules Modules => _modules.Value;

    public abstract bool DoFullGameBackup { get; }

    protected override RelativePath PrimaryExe => Profile.Executables[0];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new LarianSynchronizer(provider, this, Modules);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider)
    {
        var profile = Profile;
        var binaries = profile.Executables[^1].Parent;

        ILibraryItemInstaller[] installers =
        [
            new GenericPatternMatchInstaller(provider)
            {
                InstallFolderTargets =
                [
                    // Pak mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/366?tab=description">ImpUI (ImprovedUI) Patch7Ready</see>
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/11373?tab=description">NPC Visual Overhaul (WIP) - NPC VO</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(LarianConstants.ModsLocationId, ""),
                        KnownSourceFolderNames = ["Mods", .. profile.ExtraModsSourceFolderNames],
                        KnownValidFileExtensions = [LarianConstants.PakFileExtension],
                        FileExtensionsToDiscard =
                        [
                            KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Pdf, KnownExtensions.Png,
                            KnownExtensions.Json, new Extension(".lnk"),
                        ],
                    },

                    // bin and NativeMods mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/944">Native Mod Loader</see>
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/668?tab=files">Achievement Enabler</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(LocationId.Game, binaries),
                        KnownSourceFolderNames = [binaries.FileName.ToString()],
                        Names = ["NativeMods"],
                    },

                    // loose files Data mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/555?tab=description">Fast XP</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(LocationId.Game, profile.GameData),
                        KnownSourceFolderNames = [profile.GameData.FileName.ToString()],
                        Names = ["Generated", "Public"],
                    },
                ],
            },
        ];

        return profile.ScriptExtender is { } extender
            ? [new LarianScriptExtenderInstaller(provider, profile.GameName, extender), .. installers]
            : installers;
    }

    protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) =>
        Profile.Schema is null ? [] : [new LarianPakSortOrderVariety(provider, this)];

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider)
    {
        foreach (var emitter in base.CreateEmitters(provider)) yield return emitter;

        yield return ActivatorUtilities.CreateInstance<LarianDependencyEmitter>(provider, Profile, TSelf.NexusModsGameId);
        if (Profile.Schema is null) yield break;

        yield return new DuplicateModuleEmitter(this, Modules);
        yield return new LarianModListReplacedEmitter(this);
        yield return new LarianModListResetEmitter(this);
    }

    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        // The Windows build keeps its data under the prefix's known folders, which the Wine file system maps for us.
        // On the host file system the same known folder is where a native build keeps it.
        var larian = fileSystem.GetKnownPath(Profile.LarianRoot).Combine(Profile.LarianFolder);

        var locations = new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path },
            { LarianConstants.ModsLocationId, larian.Combine(Profile.ModsFolder) },
            {
                LarianConstants.PlayerProfilesLocationId, larian.Combine(Profile.Profiles switch
                {
                    LarianProfileLayout.Fixed fixedLayout => fixedLayout.ProfileFolder,
                    LarianProfileLayout.PerProfileFolder perProfile => perProfile.ProfilesFolder,
                    _ => throw new InvalidOperationException($"Unknown profile layout {Profile.Profiles}"),
                })
            },
        };
        if (Profile.ScriptExtender is { } extender)
            locations.Add(LarianConstants.ScriptExtenderConfigLocationId, larian.Combine(extender.ConfigFolder));

        return locations.ToImmutableDictionary();
    }

    // The Windows build's primary file is the launcher; the engine binaries are in the last executable's folder.
    public virtual IReadOnlyList<GamePath> ExecutableFolders(GameInstallation installation) =>
        [GetPrimaryFile(installation).Parent, new GamePath(LocationId.Game, Profile.Executables[^1].Parent)];

    // The extender's DLL replaces a system DLL that Wine would otherwise load itself.
    // It is Wine that needs the override, so it applies to every store.
    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout)
    {
        if (Profile.ScriptExtender is not { Override: { } required } extender) return [];

        // The native build has no Wine to configure; the script extender can't load there at all.
        if (LarianBuild.IsNative(loadout.InstallationInstance.LocatorResult)) return [];
        return ScriptExtenderFile.Find(loadout, extender.DllPath).HasValue ? [required] : [];
    }
}
