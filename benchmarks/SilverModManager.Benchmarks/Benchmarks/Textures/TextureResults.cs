using System.Collections.Concurrent;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Benchmarks.Benchmarks.Textures;

/// <summary>
/// What a benchmark method knows that BenchmarkDotNet's columns cannot: how many pixels one invocation encodes and the
/// last output it produced, from which the PSNR column is decoded after the run. Keyed by the case's display text.
/// </summary>
internal static class TextureResults
{
    internal sealed record Entry(double Pixels, byte[] Reference, int Width, int Height, BlockFormat Format)
    {
        public byte[]? Blocks { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    public static void Expect(string key, Entry entry) => Entries[key] = entry;

    public static void Record(string key, byte[] blocks)
    {
        if (Entries.TryGetValue(key, out var entry)) entry.Blocks = blocks;
    }

    public static Entry? Find(BenchmarkCase benchmarkCase)
        => benchmarkCase.Parameters.Items.Select(p => p.Value?.ToString()).FirstOrDefault(k => k is not null && Entries.ContainsKey(k)) is { } key ? Entries[key] : null;
}

/// <summary>Top-level pixels encoded per second of the median iteration, in millions; mips are not counted.</summary>
internal sealed class MpixPerSecondColumn : IColumn
{
    public string Id => nameof(MpixPerSecondColumn);
    public string ColumnName => "Mpix/s";
    public string Legend => "Millions of top-level pixels encoded per second, by the median iteration";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var entry = TextureResults.Find(benchmarkCase);
        var stats = summary[benchmarkCase]?.ResultStatistics;
        return entry is null || stats is null ? "?" : (entry.Pixels / (stats.Median * 1e-9) / 1e6).ToString("F2", style.CultureInfo);
    }
}

internal sealed class PsnrColumn : IColumn
{
    public string Id => nameof(PsnrColumn);
    public string ColumnName => "PSNR dB";
    public string Legend => "The last output decoded against the level it was encoded from (BC1 RGB, BC4 R, BC5 RG, else RGBA)";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 1;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => TextureResults.Find(benchmarkCase) is { Blocks: { } blocks } e
            ? TextureQuality.Psnr(e.Reference, blocks, e.Width, e.Height, e.Format).ToString("F2", style.CultureInfo)
            : "?";
}
