namespace SMLoader.Api;

/// <summary>
/// A game script caught on its way to the Lua compiler. Rewrite
/// <see cref="Source"/> to change what the engine actually compiles - the file
/// on disk is never read back, modified or replaced.
/// </summary>
public sealed class ScriptLoadContext
{
    public ScriptLoadContext(string name, string source)
    {
        Name = name;
        Source = source;
    }

    /// <summary>
    /// Chunk name the engine passed, e.g.
    /// <c>@$GAME_DATA/Scripts/game/CreativePlayer.lua</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>The Lua source about to be compiled. Assign to rewrite it.</summary>
    public string Source { get; set; }

    /// <summary>Appends Lua source after the original chunk.</summary>
    public void Append(string lua) => Source += Environment.NewLine + lua + Environment.NewLine;

    /// <summary>Inserts Lua source before the original chunk.</summary>
    public void Prepend(string lua) => Source = lua + Environment.NewLine + Source;

    /// <summary>
    /// Replaces the first occurrence of <paramref name="find"/>. Returns false
    /// if it was not present, which usually means a game update moved it.
    /// </summary>
    public bool ReplaceFirst(string find, string replacement)
    {
        int index = Source.IndexOf(find, StringComparison.Ordinal);
        if (index < 0)
            return false;

        Source = Source[..index] + replacement + Source[(index + find.Length)..];
        return true;
    }
}
