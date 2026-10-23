namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// The order directives are written in, whatever order the file lists them: built archives need the loose files
/// other directives produce, and merges need the installed sources.
/// </summary>
public static class WriteOrder
{
    public static int Of(Directive directive) => directive switch
    {
        CreateBsa => 1,
        MergedPatch => 2,
        _ => 0,
    };
}
