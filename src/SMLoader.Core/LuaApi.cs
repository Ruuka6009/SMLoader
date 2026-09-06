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

    private static readonly List<(string Name, LuaFunction Function)> Functions = new();
    private static readonly Dictionary<nint, int> TableRefs = new();
    private static readonly Lock Gate = new();
    private static int _seededEnvironments;

    public static void Add(string modName, string name, LuaFunction function)
    {
        lock (Gate)
        {
            Functions.Add((name, function));

            // Any table already built for a state is now stale; drop the cache
            // so the next environment picks the new function up.
            TableRefs.Clear();
        }

        Logging.Write($"[{modName}] exposed {TableName}.{name}()");
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
        if (reference == 0)
            return;

        var lua = new LuaState(L);
        lua.Push(TableName);                             // env, "smloader"
        lua_rawgeti(L, LUA_REGISTRYINDEX, reference);    // env, "smloader", table
        lua_rawset(L, -3);                               // env

        int count = Interlocked.Increment(ref _seededEnvironments);
        if (count == 1)
            Logging.Write($"seeded '{TableName}' into the first script environment");
    }

    private static int GetOrCreateTable(nint L)
    {
        lock (Gate)
        {
            if (TableRefs.TryGetValue(L, out int existing))
                return existing;

            var lua = new LuaState(L);
            lua.NewTable();

            foreach ((string name, LuaFunction function) in Functions)
                lua.SetFunction(name, function);

            // luaL_ref pops the table and hands back a registry slot, so the
            // table survives without living in any script's environment.
            int reference = luaL_ref(L, LUA_REGISTRYINDEX);
            TableRefs[L] = reference;
            return reference;
        }
    }
}
