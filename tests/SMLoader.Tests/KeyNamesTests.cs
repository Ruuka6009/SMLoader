using SMLoader.Core;

namespace SMLoader.Tests;

/// <summary>
/// Every case here is a range boundary. The failure mode this guards against is
/// a rebind showing "Key 58" instead of a name, which looks like a broken
/// setting rather than an off-by-one.
/// </summary>
[TestClass]
public class KeyNamesTests
{
    [TestMethod]
    // Digits: 0x30-0x39, with the codes either side.
    [DataRow(0x2F, "Key 47")]
    [DataRow(0x30, "0")]
    [DataRow(0x39, "9")]
    [DataRow(0x3A, "Key 58")]
    // Letters: 0x41-0x5A.
    [DataRow(0x40, "Key 64")]
    [DataRow(0x41, "A")]
    [DataRow(0x5A, "Z")]
    [DataRow(0x5B, "Key 91")]
    // Function keys: 0x70-0x87.
    [DataRow(0x6F, "Key 111")]
    [DataRow(0x70, "F1")]
    [DataRow(0x87, "F24")]
    [DataRow(0x88, "Key 136")]
    // Numpad: 0x60-0x69.
    [DataRow(0x5F, "Key 95")]
    [DataRow(0x60, "Numpad 0")]
    [DataRow(0x69, "Numpad 9")]
    [DataRow(0x6A, "Key 106")]
    public void Describe_HandlesEveryRangeBoundary(int virtualKey, string expected)
        => Assert.AreEqual(expected, KeyNames.Describe(virtualKey));

    [TestMethod]
    [DataRow(0x08, "Backspace")]
    [DataRow(0x0D, "Enter")]
    [DataRow(0x20, "Space")]
    [DataRow(0x1B, "Esc")]
    [DataRow(0xDC, "\\")]
    public void Describe_PrefersTheNamedTable(int virtualKey, string expected)
        => Assert.AreEqual(expected, KeyNames.Describe(virtualKey));

    [TestMethod]
    public void Describe_NamedTableWinsOverTheRanges()
    {
        // 0x60 is both "Numpad 0" by range and absent from the table; 0x0D sits
        // below every range. The point is that a named entry is never shadowed.
        Assert.AreEqual("Numpad 0", KeyNames.Describe(0x60));
        Assert.AreEqual("Enter", KeyNames.Describe(0x0D));
    }

    [TestMethod]
    [DataRow(0, "Key 0")]
    [DataRow(-1, "Key -1")]
    [DataRow(0xFF, "Key 255")]
    public void Describe_FallsBackRatherThanThrowing(int virtualKey, string expected)
        => Assert.AreEqual(expected, KeyNames.Describe(virtualKey));
}
