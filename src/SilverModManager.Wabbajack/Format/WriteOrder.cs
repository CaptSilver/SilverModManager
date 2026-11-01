namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// The order directives are written in, whatever order the file lists them: built archives need the loose files
/// other directives produce, and merges need the installed sources.
/// </summary>
public static class WriteOrder
{
    /// <summary>The kind that builds archives from loose files.</summary>
    public const int Archive = 1;

    /// <summary>The kind that joins installed files and patches the result; the last, since it reads what the others wrote.</summary>
    public const int Merge = 2;

    public static int Of(Directive directive) => directive switch
    {
        CreateBsa => Archive,
        MergedPatch => Merge,
        _ => 0,
    };
}
