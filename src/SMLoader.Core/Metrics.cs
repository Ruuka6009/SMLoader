using System.Diagnostics;
using System.Text;

namespace SMLoader.Core;

/// <summary>
/// Cheap counters for the paths this loader was optimised on.
/// </summary>
/// <remarks>
/// The point is stated in OPTIMISATION.md §11.4: without numbers, every claim in
/// its performance section is an argument rather than a measurement. Several of
/// those claims — "~1,500 allocations a second", "over 4,000 syscalls a second" —
/// were derived from reading the code, and one of them (§2.2) turned out to be
/// worth less than the correctness it cost.
/// <para>
/// Every counter is a single <see cref="Interlocked"/> increment on a path that
/// already does far more than that. The transform timer uses
/// <see cref="Stopwatch.GetTimestamp"/> rather than allocating a Stopwatch.
/// </para>
/// </remarks>
internal static class Metrics
{
    private static long _luaCalls;
    private static long _scriptCompiles;
    private static long _scriptCacheHits;
    private static long _scriptTransformTicks;
    private static long _fileOpens;
    private static long _fileRedirects;
    private static long _assetCacheHits;
    private static long _statesCreated;
    private static long _statesClosed;
    private static long _modLoadMs;

    public static void LuaCall() => Interlocked.Increment(ref _luaCalls);

    public static void ScriptCompile(bool cacheHit)
    {
        Interlocked.Increment(ref _scriptCompiles);
        if (cacheHit)
            Interlocked.Increment(ref _scriptCacheHits);
    }

    public static void ScriptTransformTime(long ticks)
        => Interlocked.Add(ref _scriptTransformTicks, ticks);

    public static void FileOpen(bool redirected, bool cacheHit)
    {
        Interlocked.Increment(ref _fileOpens);
        if (redirected)
            Interlocked.Increment(ref _fileRedirects);
        if (cacheHit)
            Interlocked.Increment(ref _assetCacheHits);
    }

    public static void StateCreated() => Interlocked.Increment(ref _statesCreated);

    public static void StateClosed() => Interlocked.Increment(ref _statesClosed);

    public static void ModLoadTime(long milliseconds) => Interlocked.Add(ref _modLoadMs, milliseconds);

    /// <summary>
    /// One line per group, in the order the work happens. Reads are not a
    /// consistent snapshot across counters, which is fine: these answer "is this
    /// path hot" and "is the cache working", not accounting questions.
    /// </summary>
    public static string Report()
    {
        long compiles = Volatile.Read(ref _scriptCompiles);
        long hits = Volatile.Read(ref _scriptCacheHits);
        long opens = Volatile.Read(ref _fileOpens);
        long transformTicks = Volatile.Read(ref _scriptTransformTicks);

        double transformMs = transformTicks * 1000.0 / Stopwatch.Frequency;

        var text = new StringBuilder();
        text.Append($"lua calls {Volatile.Read(ref _luaCalls)}; ");
        text.Append($"states {Volatile.Read(ref _statesCreated)} created / " +
                    $"{Volatile.Read(ref _statesClosed)} closed; ");
        text.Append($"script compiles {compiles} ({Percent(hits, compiles)} cached, " +
                    $"{transformMs:F1}ms transforming); ");
        text.Append($"file opens reaching managed code {opens} " +
                    $"({Volatile.Read(ref _fileRedirects)} redirected, " +
                    $"{Percent(Volatile.Read(ref _assetCacheHits), opens)} cached); ");
        text.Append($"mods loaded in {Volatile.Read(ref _modLoadMs)}ms");
        return text.ToString();
    }

    private static string Percent(long part, long whole)
        => whole == 0 ? "0%" : $"{part * 100.0 / whole:F0}%";
}
