using SMLoader.Api;

namespace SMLoader.Tests;

/// <summary>
/// <see cref="ScriptLoadContext"/> is the surface every script-patching mod
/// writes against, and it is backed by a StringBuilder that only flattens when
/// <see cref="ScriptLoadContext.Source"/> is read. These pin down both the text
/// it produces and the bookkeeping the patcher relies on to avoid reading it.
/// </summary>
[TestClass]
public class ScriptLoadContextTests
{
    private static ScriptLoadContext New(string source = "body") => new("chunk.lua", source);

    [TestMethod]
    public void Append_PutsTextAfterTheOriginal()
    {
        ScriptLoadContext context = New();
        context.Append("tail");

        Assert.AreEqual($"body{Environment.NewLine}tail{Environment.NewLine}", context.Source);
    }

    [TestMethod]
    public void Prepend_PutsTextBeforeTheOriginal()
    {
        ScriptLoadContext context = New();
        context.Prepend("head");

        Assert.AreEqual($"head{Environment.NewLine}body", context.Source);
    }

    [TestMethod]
    public void AppendsAndPrepends_Compose()
    {
        ScriptLoadContext context = New();
        context.Append("one");
        context.Append("two");
        context.Prepend("zero");

        Assert.AreEqual(
            $"zero{Environment.NewLine}body{Environment.NewLine}one{Environment.NewLine}" +
            $"{Environment.NewLine}two{Environment.NewLine}",
            context.Source);
    }

    [TestMethod]
    public void ReplaceFirst_ReplacesOnlyTheFirstOccurrence()
    {
        ScriptLoadContext context = New("a x a x a");

        Assert.IsTrue(context.ReplaceFirst("x", "Y"));
        Assert.AreEqual("a Y a x a", context.Source);
    }

    [TestMethod]
    public void ReplaceFirst_ReturnsFalseAndChangesNothingWhenAbsent()
    {
        ScriptLoadContext context = New("a b c");

        Assert.IsFalse(context.ReplaceFirst("zzz", "Y"));
        Assert.AreEqual("a b c", context.Source);
        Assert.AreEqual(0, context.Revision, "a failed replace is not a mutation");
    }

    [TestMethod]
    public void ReplaceFirst_SeesTextAddedByAppend()
    {
        // Regression guard: Source has to flatten the builder before searching,
        // or a replace would silently miss anything a previous mod appended.
        ScriptLoadContext context = New();
        context.Append("marker");

        Assert.IsTrue(context.ReplaceFirst("marker", "replaced"));
        StringAssert.Contains(context.Source, "replaced");
    }

    [TestMethod]
    public void Revision_CountsEveryMutationAndNothingElse()
    {
        ScriptLoadContext context = New();
        Assert.AreEqual(0, context.Revision);

        context.Append("one");
        Assert.AreEqual(1, context.Revision);

        context.Prepend("two");
        Assert.AreEqual(2, context.Revision);

        context.Source = "wholesale";
        Assert.AreEqual(3, context.Revision);

        _ = context.Source;
        _ = context.Length;
        Assert.AreEqual(3, context.Revision, "reading is not mutating");
    }

    [TestMethod]
    public void Length_MatchesSourceWithoutFlatteningTheBuilder()
    {
        // ScriptPatcher uses Length precisely so it does not have to touch
        // Source between one mod's turn and the next.
        ScriptLoadContext context = New();
        context.Append("some appended text");

        int lengthBeforeReadingSource = context.Length;
        Assert.AreEqual(lengthBeforeReadingSource, context.Source.Length);
    }

    [TestMethod]
    public void Name_IsCarriedThrough()
        => Assert.AreEqual("chunk.lua", New().Name);
}
