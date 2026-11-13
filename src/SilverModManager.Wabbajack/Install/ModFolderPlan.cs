namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// One mod folder of a list as the install will do it, for the rows the page shows.
/// </summary>
/// <param name="Folder">The folder's name, which is also the group the loadout files it under.</param>
/// <param name="Index">The folder's place in the list; the rows show in this order.</param>
/// <param name="Total">Files the folder has in the list.</param>
/// <param name="AlreadyDone">Of those, files an earlier run already wrote.</param>
/// <param name="LaterPasses">Files written after the loose ones: archives to build and merges.</param>
/// <param name="MergePasses">Of the later passes, the merges.</param>
public sealed record ModFolderPlan(string Folder, int Index, int Total, int AlreadyDone, int LaterPasses, int MergePasses);
