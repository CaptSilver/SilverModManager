namespace NexusMods.Platform.Proton.ToolRunner;

/// <summary>
/// A strategy for a prefix kind that is only tried when the regular strategy for that kind cannot run
/// the program.
/// </summary>
public interface ILastResortToolRunStrategy : IToolRunStrategy;
