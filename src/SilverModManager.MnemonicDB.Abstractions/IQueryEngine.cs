using System.Threading.Tasks;
using NexusMods.HyperDuck;

namespace SilverModManager.MnemonicDB.Abstractions;

public interface IQueryEngine
{
    public DuckDB DuckDb { get; }
    
    public LogicalType AttrEnum { get; }
}
