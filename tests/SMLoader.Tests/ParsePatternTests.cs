using SMLoader.Core;

namespace SMLoader.Tests;

/// <summary>
/// Pattern parsing decides where a mod writes into the game's code. A silently
/// mis-parsed pattern does not throw - it scans for the wrong bytes and either
/// finds nothing or, worse, finds something.
/// </summary>
[TestClass]
public class ParsePatternTests
{
    [TestMethod]
    public void ParsesPlainHexBytes()
    {
        (byte[] bytes, bool[] wildcard) = ProcessMemory.ParsePattern("48 8B 05");

        CollectionAssert.AreEqual(new byte[] { 0x48, 0x8B, 0x05 }, bytes);
        CollectionAssert.AreEqual(new[] { false, false, false }, wildcard);
    }

    [TestMethod]
    [DataRow("48 ?? 05")]
    [DataRow("48 ? 05")]
    public void BothWildcardSpellingsAreAccepted(string pattern)
    {
        (byte[] bytes, bool[] wildcard) = ProcessMemory.ParsePattern(pattern);

        Assert.AreEqual(3, bytes.Length);
        CollectionAssert.AreEqual(new[] { false, true, false }, wildcard);
        Assert.AreEqual(0, bytes[1], "a wildcard slot carries no byte value");
    }

    [TestMethod]
    public void LowercaseHexIsAccepted()
    {
        (byte[] bytes, _) = ProcessMemory.ParsePattern("4d 8b c0");

        CollectionAssert.AreEqual(new byte[] { 0x4D, 0x8B, 0xC0 }, bytes);
    }

    [TestMethod]
    public void RunsOfWhitespaceAreNotEmptyTokens()
    {
        (byte[] bytes, _) = ProcessMemory.ParsePattern("  48   8B  ");

        CollectionAssert.AreEqual(new byte[] { 0x48, 0x8B }, bytes);
    }

    [TestMethod]
    [DataRow("48 ZZ 05", DisplayName = "invalid hex")]
    [DataRow("48 123 05", DisplayName = "does not fit in a byte")]
    [DataRow("hello", DisplayName = "not hex at all")]
    public void AnUnparseablePatternYieldsNothingRatherThanAGuess(string pattern)
    {
        (byte[] bytes, bool[] wildcard) = ProcessMemory.ParsePattern(pattern);

        // FindPattern treats an empty result as "no match", so a typo fails the
        // scan instead of scanning for something the author did not write.
        Assert.AreEqual(0, bytes.Length);
        Assert.AreEqual(0, wildcard.Length);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void AnEmptyPatternYieldsNothing(string pattern)
        => Assert.AreEqual(0, ProcessMemory.ParsePattern(pattern).Bytes.Length);

    [TestMethod]
    public void AnAllWildcardPatternParsesButMatchesAnything()
    {
        // Worth pinning: this parses successfully, so the "did it parse" check
        // will not catch it. It is a caller mistake, not a parser one.
        (byte[] bytes, bool[] wildcard) = ProcessMemory.ParsePattern("?? ?? ??");

        Assert.AreEqual(3, bytes.Length);
        Assert.IsTrue(wildcard.All(w => w));
    }

    [TestMethod]
    public void BoundaryByteValuesRoundTrip()
    {
        (byte[] bytes, _) = ProcessMemory.ParsePattern("00 0F FF");

        CollectionAssert.AreEqual(new byte[] { 0x00, 0x0F, 0xFF }, bytes);
    }
}
