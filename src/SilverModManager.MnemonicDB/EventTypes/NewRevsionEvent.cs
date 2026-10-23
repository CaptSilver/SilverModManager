using System.Threading;
using System.Threading.Tasks;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.MnemonicDB.EventTypes;

/// <summary>
/// A new DB revision event
/// </summary>
internal record NewRevisionEvent(IDb? Prev, IDb Db, ManualResetEventSlim OnFinished) : IEvent;
