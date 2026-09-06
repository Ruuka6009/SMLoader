namespace SMLoader.Api;

/// <summary>How a setting should be presented and edited.</summary>
public enum SettingKind
{
    /// <summary>On/off.</summary>
    Toggle,

    /// <summary>A Windows virtual-key code, edited by pressing a key.</summary>
    Key,

    /// <summary>A number, optionally bounded by <see cref="ModSetting.Minimum"/>/<see cref="ModSetting.Maximum"/>.</summary>
    Number,

    /// <summary>One of <see cref="ModSetting.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// A setting a mod exposes for configuration. Declaring one is enough: SMLoader
/// persists it, exposes it to the in-game settings panel, and hands the mod the
/// current value on request.
/// </summary>
/// <param name="Key">Storage key, unique within the mod.</param>
/// <param name="Label">Human-readable name shown in the settings panel.</param>
/// <param name="Kind">How it is edited.</param>
/// <param name="Default">Value used until the player changes it.</param>
public sealed record ModSetting(
    string Key,
    string Label,
    SettingKind Kind,
    object Default)
{
    /// <summary>Optional one-line explanation shown alongside the row.</summary>
    public string? Description { get; init; }

    /// <summary>Lower bound for <see cref="SettingKind.Number"/>.</summary>
    public double Minimum { get; init; } = double.MinValue;

    /// <summary>Upper bound for <see cref="SettingKind.Number"/>.</summary>
    public double Maximum { get; init; } = double.MaxValue;

    /// <summary>Increment used by the panel for <see cref="SettingKind.Number"/>.</summary>
    public double Step { get; init; } = 1.0;

    /// <summary>Options for <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A declared setting together with the mod that owns it and its current value.
/// This is what the settings panel enumerates.
/// </summary>
public sealed class ModSettingEntry
{
    public ModSettingEntry(string modName, ModSetting definition, Func<object> read, Action<object> write)
    {
        ModName = modName;
        Definition = definition;
        _read = read;
        _write = write;
    }

    private readonly Func<object> _read;
    private readonly Action<object> _write;

    public string ModName { get; }

    public ModSetting Definition { get; }

    public object Value
    {
        get => _read();
        set => _write(value);
    }
}
