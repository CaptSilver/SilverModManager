using System;
using DynamicData;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;

namespace SilverModManager.MnemonicDB.EventTypes;

/// <summary>
/// Request to unsubscribe the given observer from the database.
/// </summary>
public record UnSubscribeEvent(IObserver<ChangeSet<Datom, DatomKey, IDb>> Observer) : IEvent;
