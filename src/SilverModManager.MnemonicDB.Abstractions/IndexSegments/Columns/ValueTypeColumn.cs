using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;

namespace SilverModManager.MnemonicDB.Abstractions.IndexSegments.Columns;

public class ValueTypeColumn : ASimpleColumn<ValueTag>
{
    public static readonly ValueTypeColumn Instance = new();
    
    public override ValueTag GetValue(in KeyPrefix prefix)
    {
        return prefix.ValueTag;
    }
}
