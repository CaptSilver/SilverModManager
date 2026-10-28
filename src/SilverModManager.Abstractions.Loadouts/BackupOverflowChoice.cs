namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// What to do with added files that together exceed the backup cap. Lives beside <see cref="Loadouts.ISynchronizerService"/>
/// because the service takes it as a preset; the namespace is the one the rest of the overflow types use.
/// </summary>
public enum BackupOverflowChoice
{
    /// <summary>Archive every file, whatever the total.</summary>
    BackUpAnyway,

    /// <summary>Keep files that only want a copy where they are and skip the archive; everything else is still backed up.</summary>
    AdoptInPlace,

    /// <summary>Do nothing and say why.</summary>
    Refuse,
}
