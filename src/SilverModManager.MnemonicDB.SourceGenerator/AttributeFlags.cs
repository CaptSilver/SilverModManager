using System;

namespace SilverModManager.MnemonicDB.SourceGenerator;

[Flags]
internal enum AttributeFlags
{
    Marker = 1,
    Scalar = 2,
    Collection = 4,
    Reference = 8,
}
