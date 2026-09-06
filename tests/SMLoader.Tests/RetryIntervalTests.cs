namespace SMLoader.Tests;

/// <summary>
/// GameKeybinds decides whether to re-read the file with an elapsed-time test.
/// It is not directly testable — it reaches the filesystem and holds static
/// state — so these pin down the arithmetic that broke it, which is the part
/// that actually went wrong.
/// </summary>
/// <remarks>
/// The bug: <c>_lastAttempt</c> was seeded with <see cref="long.MinValue"/> to
/// force a first read. <c>now - long.MinValue</c> overflows to a large negative
/// number, so <c>elapsed >= interval</c> was false forever and the file was never
/// read. Every binding fell back to QWERTY, which on an AZERTY keyboard broke
/// ZQSD movement while leaving Space and Ctrl working — their fallbacks are the
/// same on both layouts, so the failure looked like a movement bug rather than a
/// keybind one.
/// </remarks>
[TestClass]
public class RetryIntervalTests
{
    private const long RetryMs = 10_000;

    /// <summary>The shape GameKeybinds actually uses now.</summary>
    private static bool ShouldAttempt(bool attempted, long now, long lastAttempt)
        => !attempted || now - lastAttempt >= RetryMs;

    [TestMethod]
    public void TheFirstCallAlwaysAttempts()
    {
        // Including on a machine whose uptime is under the retry interval, which
        // a plain "now - 0 >= RetryMs" would have got wrong too.
        Assert.IsTrue(ShouldAttempt(attempted: false, now: 0, lastAttempt: 0));
        Assert.IsTrue(ShouldAttempt(attempted: false, now: 5, lastAttempt: 0));
        Assert.IsTrue(ShouldAttempt(attempted: false, now: 1_000_000, lastAttempt: 0));
    }

    [TestMethod]
    public void SeedingWithMinValueWouldNeverHaveAttempted()
    {
        // The regression itself, kept as an executable statement of why the
        // separate flag exists. Signed overflow is unchecked in C#, so this
        // silently yields a negative elapsed time.
        long now = 500_000;
        long elapsed = unchecked(now - long.MinValue);

        Assert.IsTrue(elapsed < 0, "the subtraction is expected to overflow negative");
        Assert.IsFalse(elapsed >= RetryMs, "which is why the interval test never fired");
    }

    [TestMethod]
    public void AnAttemptInsideTheIntervalIsSkipped()
        => Assert.IsFalse(ShouldAttempt(attempted: true, now: 5_000, lastAttempt: 0));

    [TestMethod]
    public void AnAttemptAtExactlyTheIntervalRuns()
        => Assert.IsTrue(ShouldAttempt(attempted: true, now: RetryMs, lastAttempt: 0));

    [TestMethod]
    public void AnAttemptPastTheIntervalRuns()
        => Assert.IsTrue(ShouldAttempt(attempted: true, now: RetryMs + 1, lastAttempt: 0));

    [TestMethod]
    public void LargeUptimesDoNotOverflow()
    {
        // TickCount64 is milliseconds since boot and does not wrap in any
        // plausible uptime, but the arithmetic should not be delicate about it.
        const long days100 = 100L * 24 * 60 * 60 * 1000;

        Assert.IsTrue(ShouldAttempt(attempted: true, now: days100, lastAttempt: days100 - RetryMs));
        Assert.IsFalse(ShouldAttempt(attempted: true, now: days100, lastAttempt: days100 - 1));
    }
}
