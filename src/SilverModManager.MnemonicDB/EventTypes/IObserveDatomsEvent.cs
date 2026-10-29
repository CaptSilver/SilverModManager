using System;
using DynamicData;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;

namespace SilverModManager.MnemonicDB.EventTypes;

public interface IObserveDatomsEvent : IEvent
{
    /// <summary>
    /// Names the slice being observed (descriptor type, entity range and attribute), for logs that have to say which
    /// subscription a problem belongs to.
    /// </summary>
    internal string Describe(IDb db);

    internal (Datom From, Datom To, IObserver<ChangeSet<Datom, DatomKey, IDb>> Observer) Prime(IDb db);
}
