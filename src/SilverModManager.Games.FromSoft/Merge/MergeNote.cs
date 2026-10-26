namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Something a merge did that the user should hear about: a choice between two edits, or content taken whole. Notes are
/// structured so a caller can group or filter them; <see cref="Describe"/> is the sentence a report prints.
/// </summary>
public interface IMergeNote
{
    string Describe();
}

/// <summary>The game and a mod each added a different row under one id, with no ancestor row to locate either edit against.</summary>
public sealed record RowCollision(int EntryId, int RowId) : IMergeNote
{
    public string Describe() =>
        $"entry {EntryId} row {RowId}: the game and a mod each added a different row under this id; kept the mod's, dropped the game's";
}

/// <summary>Two mods each added a different row under one id, with no ancestor row to locate either edit against. The leading mod's row is kept.</summary>
public sealed record RivalRow(int EntryId, int RowId) : IMergeNote
{
    public string Describe() =>
        $"entry {EntryId} row {RowId}: two mods each added a different row under this id; kept the preferred mod's, dropped the other's";
}

/// <summary>A param entry the reader refuses where only the other side had an edit, so its whole table was taken instead of merged row by row.</summary>
public sealed record UnreadableParam(int EntryId) : IMergeNote
{
    public string Describe() =>
        $"entry {EntryId} can't be read as a PARAM; the base still matched vanilla there, so the mod's whole table was taken instead of merging it row by row";
}

/// <summary>Both sides changed one row differently; the winner's row was kept whole.</summary>
/// <param name="KeptMod">Who owned the row that stayed: the higher-priority mod, or the game on the first step of a fold.</param>
/// <param name="DroppedMod">Whose edits to the row were dropped.</param>
/// <param name="KeptIsGame">The row that stayed is the game's own, so the conflict is a mod against the game rather than two mods.</param>
public sealed record RowConflict(int EntryId, int RowId, string KeptMod, string DroppedMod, bool KeptIsGame = false) : IMergeNote
{
    public string Describe() => KeptIsGame
        ? $"entry {EntryId} row {RowId}: {DroppedMod} changed it differently from the game's own row; kept the game's row and dropped {DroppedMod}'s edits to it"
        : $"entry {EntryId} row {RowId}: both mods changed it differently; kept {KeptMod}'s row and dropped {DroppedMod}'s edits to it";
}

/// <summary>
/// The game's patch and a mod built for an older build both changed one row, and the two were merged byte by byte. If
/// the patch reused the row id for something else, the result mixes two meanings of it.
/// </summary>
public sealed record CrossBuildRow(int EntryId, int RowId, string ModVersion, string GameVersion) : IMergeNote
{
    public string Describe() =>
        $"entry {EntryId} row {RowId}: the game ({GameVersion}) and a mod built for {ModVersion} both changed it, and their edits were merged byte by byte; if the patch reused this row id for something else, the row is a mix of the two";
}

/// <summary>Both mods ship a message under one id with different text; the winner's text was kept.</summary>
/// <param name="KeptMod">Whose text stayed: the higher-priority mod.</param>
/// <param name="DroppedMod">Whose text was dropped.</param>
public sealed record TextKept(int EntryId, int TextId, string KeptMod, string DroppedMod) : IMergeNote
{
    public string Describe() =>
        $"entry {EntryId} text {TextId}: both mods ship it and differ; kept {KeptMod}'s";
}

/// <summary>Both mods ship a texture under one name with different bytes; the winner's copy was kept.</summary>
public sealed record TextureKept(string Name, string KeptMod, string DroppedMod) : IMergeNote
{
    public string Describe() =>
        $"texture {Name}: both mods ship it and differ; kept {KeptMod}'s";
}

/// <summary>
/// The priority order could not be merged, and another contributor as the base could. The result is what that base
/// gives, so a row or state both sides changed stays as the base had it, not as the highest-priority mod had it.
/// </summary>
/// <param name="Base">The mod the merge was led by.</param>
/// <param name="Leader">The highest-priority mod, whose lead failed.</param>
/// <param name="Error">Why the priority order failed.</param>
public sealed record MergeBaseChanged(string Base, string Leader, string Error) : IMergeNote
{
    public string Describe() =>
        $"merged with {Base} as the base because the priority order ({Leader} first) failed: {Error}";
}

/// <summary>A note about where a merge's ancestor came from. Reports show it in the source column, not among the choices the merge made.</summary>
public interface IVanillaSourceNote : IMergeNote;

/// <summary>A read-only look at the merge used the installed game's regulation as the vanilla one; applying keeps a copy of it.</summary>
public sealed record BaselineWillBeKept(string Version) : IVanillaSourceNote
{
    public string Describe() =>
        $"the next apply keeps this regulation as the version {Version} baseline";
}

/// <summary>The vanilla copy of a file came from a vanilla dump in the library.</summary>
/// <param name="Validated">The dump's regulation is a baseline the app kept after checking it, not one the game's own files vouch for.</param>
public sealed record VanillaServedFromDump(string Path, string ArchiveName, string Version, bool Validated = false) : IVanillaSourceNote
{
    public string Describe() => Validated
        ? $"{Path}: compared against the vanilla copy in archive {ArchiveName} (version {Version}), whose regulation the app validated as vanilla but the game's files cannot confirm"
        : $"{Path}: compared against the vanilla copy in archive {ArchiveName} (version {Version})";
}

/// <summary>The vanilla copy of a packed file was read from the game's own archives, so it is the installed build's.</summary>
public sealed record VanillaServedFromGameArchive(string Path) : IVanillaSourceNote
{
    public string Describe() =>
        $"{Path}: compared against the copy in the game's own archives, which is the installed build's";
}

/// <summary>
/// Several vanilla copies of one packed file were on offer, and the one the contributors' files differ from least was
/// used. A newer copy reads a patch's own changes as the mods' edits.
/// </summary>
/// <param name="Source">Where the chosen copy came from, as a phrase.</param>
/// <param name="Distance">How far the contributors' files are from it: differing state groups, text entries or textures, summed over the contributors.</param>
/// <param name="Of">How many copies were on offer.</param>
public sealed record VanillaNearestChosen(string Source, long Distance, int Of) : IMergeNote
{
    public string Describe() =>
        $"{Of} vanilla copies were on offer; compared the mods against {Source}, which they differ from least ({Distance})";
}

/// <summary>
/// Which vanilla regulation the mods of one build were compared against. Said only when the mods of a path came from
/// more than one build, since with one the single source needs no label to say whose it is.
/// </summary>
/// <param name="Version">The build the mods were made for.</param>
/// <param name="Mods">The mods made for it.</param>
/// <param name="Source">Where the vanilla regulation came from, as a phrase.</param>
public sealed record VanillaGroupSource(string Version, IReadOnlyList<string> Mods, string Source) : IVanillaSourceNote
{
    public string Describe() => $"{(Mods.Count == 1 ? "mod" : "mods")} {string.Join(", ", Mods)} (version {Version}): {Source}";
}

/// <summary>Several vouched regulations are kept for one build, and the one nearest the mods' own rows was used as their ancestor.</summary>
/// <param name="Version">The build.</param>
/// <param name="FileName">The kept file that was used.</param>
/// <param name="Of">How many were kept.</param>
public sealed record VanillaAlternateChosen(string Version, string FileName, int Of) : IMergeNote
{
    public string Describe() =>
        $"{Of} vanilla regulations are kept for version {Version}; compared the mods against {FileName}, the one nearest to their rows";
}
