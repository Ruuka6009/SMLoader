using SMLoader.Api;

namespace SMLoader.Tests;

/// <summary>
/// The handshake decides whether a mod loads at all, so a wrong answer here is
/// either a mod refused for no reason or a mod admitted that will fail later
/// inside a Lua callback where the failure is swallowed.
/// </summary>
[TestClass]
public class ApiVersionTests
{
    [TestMethod]
    [DataRow("1.0", 1, 0)]
    [DataRow("2.0", 2, 0)]
    [DataRow("0.0", 0, 0)]
    [DataRow("12.34", 12, 34)]
    public void TryParse_AcceptsMajorMinor(string version, int major, int minor)
    {
        Assert.IsTrue(ApiVersion.TryParse(version, out int gotMajor, out int gotMinor));
        Assert.AreEqual(major, gotMajor);
        Assert.AreEqual(minor, gotMinor);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "null")]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace")]
    [DataRow("2", DisplayName = "no minor")]
    [DataRow("2.0.1", DisplayName = "three parts")]
    [DataRow("two.zero", DisplayName = "not numeric")]
    [DataRow("-1.0", DisplayName = "negative major")]
    [DataRow("2.-1", DisplayName = "negative minor")]
    public void TryParse_RefusesAnythingElseRatherThanGuessing(string? version)
        => Assert.IsFalse(ApiVersion.TryParse(version, out _, out _));

    [TestMethod]
    public void CurrentVersionIsCompatibleWithItself()
    {
        Assert.IsTrue(ApiVersion.IsCompatible(ApiVersion.Text, out string reason));
        Assert.AreEqual(string.Empty, reason);
    }

    [TestMethod]
    public void ADifferentMajorIsRefused()
    {
        string older = $"{ApiVersion.Major - 1}.0";

        Assert.IsFalse(ApiVersion.IsCompatible(older, out string reason));
        StringAssert.Contains(reason, "this loader provides");
    }

    [TestMethod]
    public void ANewerMajorIsRefused()
        => Assert.IsFalse(ApiVersion.IsCompatible($"{ApiVersion.Major + 1}.0", out _));

    [TestMethod]
    public void AnOlderMinorOfTheSameMajorIsAccepted()
    {
        // Minor versions are additive, so a mod built against less of the API
        // than this loader offers is fine.
        Assert.IsTrue(ApiVersion.IsCompatible($"{ApiVersion.Major}.{ApiVersion.Minor}", out _));
    }

    [TestMethod]
    public void ANewerMinorOfTheSameMajorIsRefused()
    {
        string newer = $"{ApiVersion.Major}.{ApiVersion.Minor + 1}";

        Assert.IsFalse(ApiVersion.IsCompatible(newer, out string reason));
        StringAssert.Contains(reason, "needs SMLoader API");
    }

    [TestMethod]
    public void AnUnparseableVersionIsRefusedAndSaysSo()
    {
        Assert.IsFalse(ApiVersion.IsCompatible("banana", out string reason));
        StringAssert.Contains(reason, "major.minor");
    }

    [TestMethod]
    public void TextMatchesTheConstants()
        => Assert.AreEqual($"{ApiVersion.Major}.{ApiVersion.Minor}", ApiVersion.Text);

    [TestMethod]
    public void TheMoreThanOneModsAreStampedWithMatchesTheLoader()
    {
        // mods/Directory.Build.props hard-codes the version it stamps, so this
        // is the thing that catches it drifting from ApiVersion.
        var stamp = typeof(SMLoaderApiVersionAttribute);
        Assert.IsNotNull(stamp, "the attribute must stay public for mods to carry it");

        Assert.IsTrue(ApiVersion.IsCompatible("2.0", out _),
                      "mods/Directory.Build.props stamps 2.0; update both together");
    }
}
