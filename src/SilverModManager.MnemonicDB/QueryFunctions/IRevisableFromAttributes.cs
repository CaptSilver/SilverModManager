using System.Collections.Generic;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.MnemonicDB.QueryFunctions;

public interface IRevisableFromAttributes
{
    void ReviseFromAttrs(IReadOnlySet<IAttribute> attrs);
}
