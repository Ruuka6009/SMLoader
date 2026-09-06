using SMLoader.Core;

namespace SMLoader.Tests;

[TestClass]
public class BannerTests
{
    private const int Width = 62;

    private static List<string> Build(params (string Key, string Value)[] facts)
        => Banner.Build("Headline",
                        facts.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [TestMethod]
    public void EveryLineIsExactlyTheBannerWidth()
    {
        List<string> lines = Build(("version", "0.1.0"), ("root", @"C:\SMLoader\dist\"));

        foreach (string line in lines)
            Assert.AreEqual(Width, line.Length, $"'{line}' is not {Width} characters");
    }

    [TestMethod]
    public void FirstAndLastLinesAreSolidRules()
    {
        List<string> lines = Build(("k", "v"));

        Assert.AreEqual(new string('#', Width), lines[0]);
        Assert.AreEqual(new string('#', Width), lines[^1]);
    }

    [TestMethod]
    public void ContentRowsAreFramedOnBothSides()
    {
        List<string> lines = Build(("k", "v"));

        foreach (string line in lines[1..^1])
        {
            Assert.AreEqual('#', line[0]);
            Assert.AreEqual('#', line[^1]);
        }
    }

    [TestMethod]
    public void HeadlineAndFactsAppear()
    {
        List<string> lines = Build(("version", "0.1.0"));
        string all = string.Join('\n', lines);

        StringAssert.Contains(all, "S M L O A D E R");
        StringAssert.Contains(all, "Headline");
        StringAssert.Contains(all, "version");
        StringAssert.Contains(all, "0.1.0");
    }

    [TestMethod]
    public void OverlongContentIsTruncatedWithAnEllipsisAndStillFits()
    {
        // A long install path is the realistic case, and the frame has to survive
        // it - a banner that runs off the edge looks like a broken loader.
        List<string> lines = Build(("root", new string('x', 200)));
        string row = lines.Single(l => l.Contains('x', StringComparison.Ordinal));

        Assert.AreEqual(Width, row.Length);
        Assert.AreEqual('#', row[0]);
        Assert.AreEqual('#', row[^1]);
        StringAssert.Contains(row, "…");
    }

    [TestMethod]
    public void ContentExactlyFillingTheRowIsNotTruncated()
    {
        // Boundary: inner width is Width - 2, and content of exactly that length
        // must be left alone rather than losing its last character to an ellipsis.
        int inner = Width - 2;
        string exact = new('y', inner - "     root      ".Length);

        List<string> lines = Build(("root", exact));
        string row = lines.Single(l => l.Contains('y', StringComparison.Ordinal));

        Assert.AreEqual(Width, row.Length);
        Assert.IsFalse(row.Contains('…', StringComparison.Ordinal));
    }

    [TestMethod]
    public void NoFactsStillProducesAWellFormedBanner()
    {
        List<string> lines = Build();

        Assert.IsTrue(lines.Count >= 6);
        foreach (string line in lines)
            Assert.AreEqual(Width, line.Length);
    }
}
