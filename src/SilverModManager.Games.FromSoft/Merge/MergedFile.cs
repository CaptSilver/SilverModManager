using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>One merged file in the merged package of the mods folder.</summary>
public sealed class MergedFile(MergePlanCache plans, string relPath)
    : MergedIntrinsicFile(plans, new GamePath(LocationId.Me3Mods, $"{Me3ModsLayout.MergedPackageId}/{relPath}"), relPath);
