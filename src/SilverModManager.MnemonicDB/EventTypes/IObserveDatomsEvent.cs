using System;
using DynamicData;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;

namespace SilverModManager.MnemonicDB.EventTypes;

public interface IObserveDatomsEvent : IEvent
{
    internal (Datom From, Datom To, IObserver<ChangeSet<Datom, DatomKey, IDb>> Observer) Prime(IDb db);
}
