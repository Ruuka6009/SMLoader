using System.Text;

namespace SMLoader.Api;

/// <summary>
/// A game script caught on its way to the Lua compiler. Rewrite
/// <see cref="Source"/> to change what the engine actually compiles - the file
/// on disk is never read back, modified or replaced.
/// </summary>
public sealed class ScriptLoadContext
{
    private string _source;

    // Appends go here rather than into _source. Several mods appending to the
    // same large script - which is exactly what CreativePlayer.lua gets - is
    // quadratic through string concatenation, and these are big files sitting on
    // the world-load critical path.
    private StringBuilder? _builder;

    public ScriptLoadContext(string name, string source)
    {
        Name = name;
        _source = source;
    }

    /// <summary>
    /// Chunk name the engine passed, e.g.
    /// <c>@$GAME_DATA/Scripts/game/CreativePlayer.lua</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Counts mutations. Lets a caller tell whether a patch changed anything
    /// without materialising <see cref="Source"/>, which is what keeps the
    /// builder above from being flattened between every mod's turn.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>Current length in characters. Never materialises the builder.</summary>
    public int Length => _builder?.Length ?? _source.Length;

    /// <summary>The Lua source about to be compiled. Assign to rewrite it.</summary>
    public string Source
    {
        get
        {
            if (_builder is not null)
            {
                _source = _builder.ToString();
                _builder = null;
            }
            return _source;
        }
        set
        {
            _source = value;
            _builder = null;
            Revision++;
        }
    }

    /// <summary>Appends Lua source after the original chunk.</summary>
    public void Append(string lua)
    {
        _builder ??= new StringBuilder(_source);
        _builder.Append(Environment.NewLine).Append(lua).Append(Environment.NewLine);
        Revision++;
    }

    /// <summary>Inserts Lua source before the original chunk.</summary>
    public void Prepend(string lua)
    {
        _builder ??= new StringBuilder(_source);
        _builder.Insert(0, Environment.NewLine).Insert(0, lua);
        Revision++;
    }

    /// <summary>
    /// Replaces the first occurrence of <paramref name="find"/>. Returns false
    /// if it was not present, which usually means a game update moved it.
    /// </summary>
    public bool ReplaceFirst(string find, string replacement)
    {
        string current = Source;

        int index = current.IndexOf(find, StringComparison.Ordinal);
        if (index < 0)
            return false;

        Source = string.Concat(current.AsSpan(0, index), replacement,
                               current.AsSpan(index + find.Length));
        return true;
    }
}
