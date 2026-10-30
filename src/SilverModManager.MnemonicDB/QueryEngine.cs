using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.HyperDuck;
using NexusMods.HyperDuck.Adaptor;
using NexusMods.HyperDuck.Adaptor.Impls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.QueryFunctions;

namespace SilverModManager.MnemonicDB;

/// <summary>
/// Container class for the DuckDB based query engine elements
/// </summary>
public class QueryEngine : IQueryEngine, IAsyncDisposable
{
    public record ActiveConnection(ushort LocalId, string Name, ushort UniqueId, IConnection Connection);
    
    private readonly Registry _registry;
    private readonly NexusMods.HyperDuck.DuckDB _db;
    private readonly IAttribute[] _declaredAttributes;
    private readonly Dictionary<string, IAttribute> _attrsByShortName;
    private LogicalType _attrEnumLogicalType;
    private LogicalType _valueTagEnum;
    private ConcurrentDictionary<ushort, ActiveConnection> _connections = new();
    private readonly ModelDefinition[] _models;
    private List<ATableFunction> _registeredTableFunctions = new();
    
    public record AttrEnumEntry(IAttribute Attribute, ushort EnumId, string ShortName);

    public readonly AttrEnumEntry[] AttrEnumEntries;

    public QueryEngine(IServiceProvider services)
    {
        _registry = new Registry(services.GetServices<IResultAdaptorFactory>(),
            services.GetServices<IRowAdaptorFactory>(), 
            services.GetServices<IValueAdaptorFactory>(),
            services.GetServices<IBindingConverter>(),
            services.GetServices<AmbientSqlFragment>());
        _db = NexusMods.HyperDuck.DuckDB.Open(_registry, services.GetServices<ATableFunction>(), services.GetServices<AScalarFunction>());
        _declaredAttributes = services.GetServices<IAttribute>().OrderBy(a => a.Id.Id).ToArray();
        _attrsByShortName = _declaredAttributes.ToDictionary(a => a.ShortName, a => a);
        _attrEnumLogicalType = LogicalType.CreateEnum(_declaredAttributes.Select(s => s.ShortName).Prepend("UNKNOWN").ToArray());
        
        var entries = new List<AttrEnumEntry>();
        entries.Add(new AttrEnumEntry(null!, 0, "UNKNOWN"));
        var idx = 1;
        foreach (var entry in _declaredAttributes)
        {
            entries.Add(new AttrEnumEntry(entry, (ushort)idx, entry.ShortName));
            idx += 1;
        }

        AttrEnumEntries = entries.ToArray();
        
        _models = services.GetServices<ModelDefinition>().ToArray();
        RegisterModels();
        DuckDb.Register(new DatomsTableFunction(this));
        DuckDb.Register(new ActiveConnectionsTable(this));
    }

    private void RegisterModels()
    { 
        foreach (var model in _models)
        {
            var tableFn = new ModelTableFunction(this, model);
            DuckDb.Register(tableFn);
            _registeredTableFunctions.Add(tableFn);
        }
    }

    public List<ATableFunction> RegisteredTableFunctions => _registeredTableFunctions;
    public Dictionary<string, IAttribute> AttributesByShortName => _attrsByShortName;
    
    public ActiveConnection[] Connections => _connections.Values.ToArray();

    internal ushort RegisterConnection(IConnection connection, string name)
    {
        var uid = DuckDb.RegisterGlobalObject(connection);
        _connections[uid] = new ActiveConnection(uid, name, uid, connection);
        return uid;
    }

    internal void RemoveConnection(ushort uid)
    {
        if (_connections.Remove(uid, out _)) 
            DuckDb.DisposeGlobalObject(uid);
    }

    internal IConnection? GetConnectionByName(string name = "default")
    {
        foreach (var (_, item) in _connections)
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                return item.Connection;
        return null;
    }

    internal IConnection? GetConnectionByUid(ushort uid)
    {
        return _connections.TryGetValue(uid, out var item) ? item.Connection : null;
    }



    public LogicalType AttrEnum => _attrEnumLogicalType;

    public int AttrEnumWidth => _attrsByShortName.Count > 255 ? 2 : 1;


    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for running queries before it gives up on closing DuckDB.
    /// </summary>
    internal TimeSpan QueryDrainWait { get; set; } = TimeSpan.FromSeconds(10);

    // HyperDuck pools its connections: a query checks one out for as long as it runs and returns it afterwards, and
    // DuckDB keeps every connection it ever made in one bag and the idle ones in another. It offers no public way to
    // ask how many are checked out, and its DisposeAsync destroys all of them without waiting.
    private static readonly FieldInfo AllConnectionsField = PoolField("_allConnections");
    private static readonly FieldInfo IdleConnectionsField = PoolField("_connections");

    private static FieldInfo PoolField(string name) =>
        typeof(NexusMods.HyperDuck.DuckDB).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"NexusMods.HyperDuck.DuckDB no longer has a field named {name}; the query drain cannot count running queries");

    private static int CountOf(object pool, FieldInfo field) =>
        ((System.Collections.ICollection)field.GetValue(pool)!).Count;

    /// <summary>
    /// The number of DuckDB connections that running queries have checked out.
    /// </summary>
    internal int ConnectionsInUse()
    {
        // Total first: a connection that returns to the pool between the two reads can only make the result too high,
        // never zero while a query is still running.
        var all = CountOf(_db, AllConnectionsField);
        var idle = CountOf(_db, IdleConnectionsField);
        return Math.Max(0, all - idle);
    }

    private int _closed;

    public async ValueTask DisposeAsync()
    {
        var deadline = DateTime.UtcNow + QueryDrainWait;
        while (ConnectionsInUse() > 0)
        {
            // A query that never finishes is a hang elsewhere. Closing DuckDB under it destroys the connection it is
            // running on and crashes the process, so DuckDB stays open until the process exits.
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _attrEnumLogicalType.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
    }

    public NexusMods.HyperDuck.DuckDB DuckDb => _db;
}
