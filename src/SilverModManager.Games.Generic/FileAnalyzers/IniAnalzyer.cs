using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.Generic.FileAnalyzers;


public class IniAnalzyer
{
    public static async Task<IniAnalysisData> AnalyzeAsync(IStreamFactory info)
    {
        await using var os = await info.GetStreamAsync();
        using var buffer = new MemoryStream();
        await os.CopyToAsync(buffer);
        return Analyze(IniDocument.Parse(buffer.ToArray()));
    }

    /// <summary>Every section name and every key name of the document, keys before the first header included.</summary>
    public static IniAnalysisData Analyze(IniDocument document)
    {
        var sections = document.Sections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = document.Lines
            .Where(l => l.Kind == IniLineKind.KeyValue)
            .Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new IniAnalysisData
        {
            Sections = sections,
            Keys = keys
        };
    }
}
