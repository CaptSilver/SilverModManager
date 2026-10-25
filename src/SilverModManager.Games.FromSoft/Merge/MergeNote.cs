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

/// <summary>A read-only look at the merge used the installed game's regulation as the vanilla one; applying keeps a copy of it.</summary>
public sealed record BaselineWillBeKept(string Version) : IMergeNote
{
    public string Describe() =>
        $"the next apply keeps this regulation as the version {Version} baseline";
}

/// <summary>The vanilla copy of a file came from a vanilla dump in the library.</summary>
public sealed record VanillaServedFromDump(string Path, string ArchiveName, string Version) : IMergeNote
{
    public string Describe() =>
        $"{Path}: compared against the vanilla copy in archive {ArchiveName} (version {Version})";
}
