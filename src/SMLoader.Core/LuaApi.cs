using SMLoader.Api.Lua;
using static SMLoader.Api.Lua.LuaNative;

namespace SMLoader.Core;

/// <summary>
/// Publishes mod functions as a <c>smloader</c> table inside every sandboxed
/// script environment, seeded from the <c>lua_setfenv</c> hook.
/// </summary>
internal static class LuaApi
{
    private const string TableName = "smloader";

    /// <summary>
    /// The registry slot holding one state's table, plus the generation of the
    /// function list it was built from. A generation mismatch means a mod has
    /// registered something since, so the table is short a function.
    /// </summary>
    private readonly record struct TableEntry(int Reference, int Generation);

    private static readonly List<(string Name, LuaFunction Function)> Functions = new();
    private static readonly Dictionary<nint, TableEntry> TableRefs = new();
    private static readonly Lock Gate = new();
    private static int _seededEnvironments;
    private static int _generation;

    public static IDisposable Add(string modName, string name, LuaFunction function)
    {
        var entry = (name, function);

        lock (Gate)
        {
            Functions.Add(entry);

            // Tables already built are now short a function. Bump the generation
            // rather than dropping the entries: releasing a registry slot is only
            // legal from the thread that owns the state, and this runs on whichever
            // thread the mod registered from. Seed does the release, on the Lua
            // thread, when it next sees a stale generation.
            _generation++;
        }

        Logging.Write($"[{modName}] exposed {TableName}.{name}()");
        return new Handle(modName, entry);
    }

    /// <summary>
    /// Removes a function. The generation bump is what matters: the tables
    /// already built for live states still contain it, and they are rebuilt on
    /// the Lua thread the next time Seed runs - which is the only thread allowed
    /// to release the old one.
    /// </summary>
    private static void Remove(string modName, (string Name, LuaFunction Function) entry)
    {
        lock (Gate)
        {
            if (!Functions.Remove(entry))
                return;

            _generation++;
        }

        Logging.Write($"[{modName}] withdrew {TableName}.{entry.Name}()");
    }

    /// <summary>Idempotent: disposing twice must not withdraw someone else's function.</summary>
    private sealed class Handle : IDisposable
    {
        private readonly string _modName;
        private (string Name, LuaFunction Function)? _entry;

        public Handle(string modName, (string Name, LuaFunction Function) entry)
        {
            _modName = modName;
            _entry = entry;
        }

        public void Dispose()
        {
            (string Name, LuaFunction Function)? entry = _entry;
            _entry = null;

            if (entry is not null)
                Remove(_modName, entry.Value);
        }
    }

    /// <summary>
    /// Called from the <c>lua_close</c> detour with the state still valid, so the
    /// registry slot can be handed back. Without this the entry would outlive the
    /// state, and the allocator reuses addresses.
    /// </summary>
    public static void Release(nint L)
    {
        TableEntry entry;
        lock (Gate)
        {
            if (!TableRefs.Remove(L, out entry))
                return;
        }

        luaL_unref(L, LUA_REGISTRYINDEX, entry.Reference);
    }

    /// <summary>
    /// Called with the environment table on top of the stack. Must leave the
    /// stack exactly as it found it.
    /// </summary>
    public static void Seed(nint L)
    {
        lock (Gate)
        {
            if (Functions.Count == 0)
                return;
        }

        if (lua_type(L, -1) != LUA_TTABLE)
            return;

        int reference = GetOrCreateTable(L);
        if (reference <= 0)
            return;

        lua_rawgeti(L, LUA_REGISTRYINDEX, reference);   // env, value
        if (!lua_istable(L, -1))
        {
            // The slot did not come back as our table, which means this address
            // is a *different* state that happens to have been allocated where a
            // closed one used to live. Never luaL_unref the stale reference: it
            // names a slot in this live state's registry that belongs to someone
            // else. Forget it and build afresh.
            lua_pop(L, 1);
            Forget(L);

            reference = GetOrCreateTable(L);
            if (reference <= 0)
                return;

            lua_rawgeti(L, LUA_REGISTRYINDEX, reference);
            if (!lua_istable(L, -1))
            {
                lua_pop(L, 1);
                Logging.Error($"could not seed '{TableName}' into a script " +
                              $"environment on lua_State 0x{L:x}");
                return;
            }
        }

        var lua = new LuaState(L);
        lua.Push(TableName);   // env, table, "smloader"
        lua_insert(L, -2);     // env, "smloader", table
        lua_rawset(L, -3);     // env

        int count = Interlocked.Increment(ref _seededEnvironments);
        if (count == 1)
            Logging.Write($"seeded '{TableName}' into the first script environment");
    }

    /// <summary>Drops a state's entry without touching its registry.</summary>
    private static void Forget(nint L)
    {
        lock (Gate)
            TableRefs.Remove(L);
    }

    private static int GetOrCreateTable(nint L)
    {
        lock (Gate)
        {
            if (TableRefs.TryGetValue(L, out TableEntry entry))
            {
                if (entry.Generation == _generation)
                    return entry.Reference;

                // We are on the Lua thread here, so this is the one place the old
                // table's slot can be given back rather than leaked.
                luaL_unref(L, LUA_REGISTRYINDEX, entry.Reference);
                TableRefs.Remove(L);
            }

            var lua = new LuaState(L);
            lua.NewTable();

            foreach ((string name, LuaFunction function) in Functions)
                lua.SetFunction(name, function);

            // luaL_ref pops the table and hands back a registry slot, so the
            // table survives without living in any script's environment.
            int reference = luaL_ref(L, LUA_REGISTRYINDEX);

            // LUA_REFNIL (-1) means the value on top was nil, LUA_NOREF (-2) that
            // the reference could not be made. Both mean the table is not there,
            // and treating either as a slot number reads an unrelated one.
            if (reference <= 0)
            {
                Logging.Error($"luaL_ref returned {reference} on lua_State 0x{L:x}; " +
                              $"'{TableName}' will be missing from this state");
                return 0;
            }

            TableRefs[L] = new TableEntry(reference, _generation);
            return reference;
        }
    }
}
