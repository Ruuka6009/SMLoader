using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static SMLoader.Api.Lua.LuaNative;

namespace SMLoader.Api.Lua;

/// <summary>A managed function callable from Lua. Returns the number of results pushed.</summary>
public delegate int LuaFunction(LuaState lua);

/// <summary>
/// Thin, allocation-light wrapper over a <c>lua_State*</c>.
/// </summary>
/// <remarks>
/// Lua is not thread-safe and the game drives it from its own thread. Only touch
/// a <see cref="LuaState"/> from inside a callback Lua invoked on you, or from a
/// hook you know runs on the game's script thread. Calling in from a worker
/// thread will corrupt the VM.
/// <para>
/// A value type on purpose. It wraps one pointer, and a mod's Lua can call in
/// ten times per rendered frame - as a class that was ~1,500 allocations a
/// second at 144 fps, purely to carry an <c>nint</c>, and the gen-0 collection
/// it eventually forces is the kind of hitch a player reads as stutter.
/// </para>
/// </remarks>
public readonly struct LuaState
{
    // Keyed on the delegate itself, by reference. Minting a fresh id per push
    // meant a function pushed into ten states per world load cost ten permanent
    // entries, each rooting the delegate and through it the mod instance.
    private static readonly ConcurrentDictionary<LuaFunction, int> Ids =
        new(ReferenceComparer.Instance);
    private static readonly ConcurrentDictionary<int, LuaFunction> ById = new();
    private static int _nextId;

    private sealed class ReferenceComparer : IEqualityComparer<LuaFunction>
    {
        public static readonly ReferenceComparer Instance = new();

        // Two delegates over the same method are still two registrations; only
        // the same instance may share an id.
        public bool Equals(LuaFunction? x, LuaFunction? y) => ReferenceEquals(x, y);

        public int GetHashCode(LuaFunction obj) => RuntimeHelpers.GetHashCode(obj);
    }

    public nint Handle { get; }

    public LuaState(nint handle) => Handle = handle;

    // ---- reading ----------------------------------------------------------

    public int  Top                  => lua_gettop(Handle);
    public int  TypeOf(int index)    => lua_type(Handle, index);
    public bool ToBoolean(int index) => lua_toboolean(Handle, index) != 0;
    public double ToNumber(int index) => lua_tonumber(Handle, index);
    public long ToInteger(int index) => lua_tointeger(Handle, index);

    /// <summary>
    /// The value at <paramref name="index"/> as a string, or null.
    /// </summary>
    /// <remarks>
    /// <b>Mutates the stack slot</b> when the value is a number: <c>lua_tolstring</c>
    /// converts it in place. Doing that to a <em>key</em> during a
    /// <c>lua_next</c> traversal breaks the traversal, because the next call
    /// sees a string where it left a number. Use
    /// <see cref="ToStringValueCopy"/> for anything you did not push yourself.
    /// </remarks>
    public string? ToStringValue(int index)
    {
        nint ptr = lua_tolstring(Handle, index, out nuint len);
        if (ptr == 0)
            return null;
        unsafe
        {
            return Encoding.UTF8.GetString((byte*)ptr, checked((int)len));
        }
    }

    /// <summary>
    /// As <see cref="ToStringValue"/>, but converts a copy, so the value at
    /// <paramref name="index"/> is left exactly as it was. Leaves the stack
    /// balanced.
    /// </summary>
    public string? ToStringValueCopy(int index)
    {
        if (!EnsureStack(1))
            return null;

        lua_pushvalue(Handle, index);   // the copy is what gets converted
        string? text = ToStringValue(-1);
        Pop();
        return text;
    }

    public string TypeNameOf(int index)
    {
        nint ptr = lua_typename(Handle, TypeOf(index));
        return ptr == 0 ? "?" : Marshal.PtrToStringUTF8(ptr) ?? "?";
    }

    // ---- writing ----------------------------------------------------------

    public void PushNil()             => lua_pushnil(Handle);
    public void Push(bool value)      => lua_pushboolean(Handle, value ? 1 : 0);
    public void Push(double value)    => lua_pushnumber(Handle, value);
    public void Push(long value)      => lua_pushinteger(Handle, (nint)value);
    public void Pop(int count = 1)    => lua_pop(Handle, count);
    public void NewTable()            => lua_createtable(Handle, 0, 0);

    /// <summary>
    /// Ensures room for <paramref name="count"/> more values, returning false if the
    /// stack cannot grow. Lua guarantees LUA_MINSTACK (20) free slots on entry
    /// to a C function, so a fixed handful of pushes is safe without this - but
    /// anything pushing a variable number is not, and overflowing the Lua stack
    /// corrupts the VM rather than raising anything catchable.
    /// </summary>
    public bool EnsureStack(int count) => lua_checkstack(Handle, count) != 0;

    public void Push(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        unsafe
        {
            fixed (byte* p = utf8)
                lua_pushlstring(Handle, (nint)p, (nuint)utf8.Length);
        }
    }

    /// <summary>Pushes a managed function onto the stack as a Lua C closure.</summary>
    public unsafe void Push(LuaFunction function)
    {
        // The same delegate pushed into a hundred states costs one entry.
        int id = Ids.GetOrAdd(function, Mint);

        lua_pushinteger(Handle, id);
        lua_pushcclosure(Handle, (nint)(delegate* unmanaged[Cdecl]<nint, int>)&Trampoline, 1);
    }

    private static int Mint(LuaFunction function)
    {
        int id = Interlocked.Increment(ref _nextId);
        ById[id] = function;
        return id;
    }

    // ---- globals and tables ----------------------------------------------

    public void GetGlobal(string name) => lua_getglobal(Handle, name);
    public void SetGlobal(string name) => lua_setglobal(Handle, name);
    public void GetField(int index, string key) => lua_getfield(Handle, index, key);
    public void SetField(int index, string key) => lua_setfield(Handle, index, key);

    /// <summary>Sets <c>tableAtTop[name] = function</c>, leaving the table on the stack.</summary>
    public void SetFunction(string name, LuaFunction function)
    {
        Push(function);
        SetField(-2, name);
    }

    /// <summary>
    /// Creates (or reuses) a global table and populates it. Leaves the stack balanced.
    /// </summary>
    public void RegisterModule(string globalName, Action<LuaState> populate)
    {
        GetGlobal(globalName);
        if (!lua_istable(Handle, -1))
        {
            Pop();
            NewTable();
        }

        populate(this);

        SetGlobal(globalName);
    }

    /// <summary>Runs a chunk of Lua source. Returns null on success, else the error text.</summary>
    public string? DoString(string source)
    {
        if (luaL_loadstring(Handle, source) != 0)
        {
            string? error = ToStringValue(-1);
            Pop();
            return error ?? "load failed";
        }

        if (lua_pcall(Handle, 0, 0, 0) != 0)
        {
            string? error = ToStringValue(-1);
            Pop();
            return error ?? "call failed";
        }

        return null;
    }

    // ---- native entry point ----------------------------------------------

    /// <summary>
    /// Single native thunk every registered <see cref="LuaFunction"/> is invoked through.
    /// The delegate id travels as upvalue 1 of the closure.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Trampoline(nint L)
    {
        // Nothing may escape into the Lua VM: an exception unwinding into
        // LuaJIT's frames, or a lua_error longjmp crossing managed frames,
        // takes the process down. Swallow and report instead.
        try
        {
            CallCounter?.Invoke();

            int id = (int)lua_tointeger(L, UpvalueIndex(1));
            if (!ById.TryGetValue(id, out LuaFunction? function))
                return 0;

            return function(new LuaState(L));
        }
        catch (Exception ex)
        {
            try { ErrorSink?.Invoke(ex); } catch { /* never throw from here */ }
            return 0;
        }
    }

    /// <summary>Receives exceptions thrown by mod code inside a Lua callback.</summary>
    public static Action<Exception>? ErrorSink { get; set; }

    /// <summary>
    /// Called once per Lua-to-managed call, for the loader's own counters.
    /// Null unless something set it, so the cost is a null check on a path
    /// that is already crossing a P/Invoke boundary.
    /// </summary>
    public static Action? CallCounter { get; set; }
}
