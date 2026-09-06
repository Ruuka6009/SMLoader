using System.Runtime.InteropServices;

namespace SMLoader.Api.Lua;

/// <summary>
/// Raw bindings to the Lua 5.1 C API exported by the game's lua51.dll (LuaJIT).
/// The DLL is already loaded in the process, so these resolve against it.
/// </summary>
public static partial class LuaNative
{
    private const string Lib = "lua51.dll";

    public const int LUA_REGISTRYINDEX = -10000;
    public const int LUA_ENVIRONINDEX  = -10001;
    public const int LUA_GLOBALSINDEX  = -10002;

    public const int LUA_TNONE          = -1;
    public const int LUA_TNIL           = 0;
    public const int LUA_TBOOLEAN       = 1;
    public const int LUA_TLIGHTUSERDATA = 2;
    public const int LUA_TNUMBER        = 3;
    public const int LUA_TSTRING        = 4;
    public const int LUA_TTABLE         = 5;
    public const int LUA_TFUNCTION      = 6;
    public const int LUA_TUSERDATA      = 7;
    public const int LUA_TTHREAD        = 8;

    /// <summary>Index of the i-th upvalue of the running C closure (1-based).</summary>
    public static int UpvalueIndex(int i) => LUA_GLOBALSINDEX - i;

    // ---- stack ------------------------------------------------------------

    [LibraryImport(Lib)] public static partial int  lua_gettop(nint L);
    [LibraryImport(Lib)] public static partial void lua_settop(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_pushvalue(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_remove(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_insert(nint L, int index);
    [LibraryImport(Lib)] public static partial int  lua_checkstack(nint L, int extra);

    // ---- type inspection --------------------------------------------------

    [LibraryImport(Lib)] public static partial int  lua_type(nint L, int index);
    [LibraryImport(Lib)] public static partial nint lua_typename(nint L, int type);
    [LibraryImport(Lib)] public static partial int  lua_toboolean(nint L, int index);
    [LibraryImport(Lib)] public static partial double lua_tonumber(nint L, int index);
    [LibraryImport(Lib)] public static partial nint lua_tointeger(nint L, int index);
    [LibraryImport(Lib)] public static partial nint lua_tolstring(nint L, int index, out nuint len);
    [LibraryImport(Lib)] public static partial nuint lua_objlen(nint L, int index);

    // ---- push -------------------------------------------------------------

    [LibraryImport(Lib)] public static partial void lua_pushnil(nint L);
    [LibraryImport(Lib)] public static partial void lua_pushnumber(nint L, double n);
    [LibraryImport(Lib)] public static partial void lua_pushinteger(nint L, nint n);
    [LibraryImport(Lib)] public static partial void lua_pushboolean(nint L, int b);
    [LibraryImport(Lib)] public static partial void lua_pushlstring(nint L, nint s, nuint len);
    [LibraryImport(Lib)] public static partial void lua_pushcclosure(nint L, nint fn, int n);

    // ---- tables -----------------------------------------------------------

    [LibraryImport(Lib)] public static partial void lua_createtable(nint L, int narr, int nrec);
    [LibraryImport(Lib)] public static partial void lua_gettable(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_settable(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_rawget(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_rawset(nint L, int index);
    [LibraryImport(Lib)] public static partial int  lua_next(nint L, int index);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void lua_getfield(nint L, int index, string k);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void lua_setfield(nint L, int index, string k);

    [LibraryImport(Lib)] public static partial nint lua_touserdata(nint L, int index);
    [LibraryImport(Lib)] public static partial void lua_rawgeti(nint L, int index, int n);
    [LibraryImport(Lib)] public static partial int  luaL_ref(nint L, int t);
    [LibraryImport(Lib)] public static partial void luaL_unref(nint L, int t, int reference);

    // ---- calling ----------------------------------------------------------

    [LibraryImport(Lib)] public static partial int lua_pcall(nint L, int nargs, int nresults, int errfunc);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int luaL_loadstring(nint L, string s);

    // ---- convenience wrappers over Lua 5.1 macros -------------------------

    public static void lua_pop(nint L, int n)          => lua_settop(L, -n - 1);
    public static void lua_getglobal(nint L, string n) => lua_getfield(L, LUA_GLOBALSINDEX, n);
    public static void lua_setglobal(nint L, string n) => lua_setfield(L, LUA_GLOBALSINDEX, n);
    public static bool lua_isnil(nint L, int index)    => lua_type(L, index) == LUA_TNIL;
    public static bool lua_istable(nint L, int index)  => lua_type(L, index) == LUA_TTABLE;
}
