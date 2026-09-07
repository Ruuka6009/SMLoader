using System.Security.Cryptography;
using SMLoader.Core;

namespace SMLoader.Tests;

/// <summary>
/// The allowlist is the one thing in the loader whose failure mode is "loads
/// something it should not have", and it is now consulted from two assemblies -
/// the launcher, before injection, and the core, after it. Worth pinning down.
/// </summary>
[TestClass]
public class ModAllowListTests
{
    private static string NewModsDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "smloader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteMod(string modsDirectory, string fileName, string content)
    {
        string path = Path.Combine(modsDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string HashOf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [TestMethod]
    public void NoFileMeansNoAllowlist()
    {
        string mods = NewModsDirectory();
        try
        {
            ModAllowList? list = ModAllowList.Load(mods, out string? failure);

            Assert.IsNull(list, "absent is the default, and the default loads everything");
            Assert.IsNull(failure);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void AMatchingHashIsAllowed()
    {
        string mods = NewModsDirectory();
        try
        {
            string mod = WriteMod(mods, "NoclipMod.dll", "pretend this is an assembly");
            File.WriteAllText(ModAllowList.PathFor(mods),
                              $$"""{ "NoclipMod.dll": "{{HashOf(mod)}}" }""");

            ModAllowList? list = ModAllowList.Load(mods, out string? failure);

            Assert.IsNull(failure);
            Assert.IsNotNull(list);
            Assert.AreEqual(1, list.Count);
            Assert.IsTrue(list.IsAllowed(mod, out string reason), reason);
            Assert.AreEqual(string.Empty, reason);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void AChangedFileIsRefusedAndTheReasonSaysSo()
    {
        string mods = NewModsDirectory();
        try
        {
            string mod = WriteMod(mods, "NoclipMod.dll", "the version that was vetted");
            string vetted = HashOf(mod);
            File.WriteAllText(ModAllowList.PathFor(mods), $$"""{ "NoclipMod.dll": "{{vetted}}" }""");

            File.WriteAllText(mod, "something else entirely");

            ModAllowList? list = ModAllowList.Load(mods, out _);

            Assert.IsNotNull(list);
            Assert.IsFalse(list.IsAllowed(mod, out string reason));
            StringAssert.Contains(reason, "does not match the allowlist");
            StringAssert.Contains(reason, vetted);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void AFileNotListedAtAllIsRefused()
    {
        string mods = NewModsDirectory();
        try
        {
            string mod = WriteMod(mods, "Unexpected.dll", "not vetted by anyone");
            File.WriteAllText(ModAllowList.PathFor(mods), """{ "NoclipMod.dll": "00" }""");

            ModAllowList? list = ModAllowList.Load(mods, out _);

            Assert.IsNotNull(list);
            Assert.IsFalse(list.IsAllowed(mod, out string reason));
            StringAssert.Contains(reason, "not in the allowlist");
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void ACorruptAllowlistRefusesEverythingRatherThanNothing()
    {
        // The whole point: a broken allowlist must not be a way past the
        // allowlist. Load returns an empty one, not null.
        string mods = NewModsDirectory();
        try
        {
            string mod = WriteMod(mods, "NoclipMod.dll", "anything");
            File.WriteAllText(ModAllowList.PathFor(mods), "{ this is not json");

            ModAllowList? list = ModAllowList.Load(mods, out string? failure);

            Assert.IsNotNull(failure);
            Assert.IsNotNull(list);
            Assert.AreEqual(0, list.Count);
            Assert.IsFalse(list.IsAllowed(mod, out _));
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void TheFileNameIsMatchedTheWayWindowsMatchesIt()
    {
        string mods = NewModsDirectory();
        try
        {
            string mod = WriteMod(mods, "NoclipMod.dll", "pretend this is an assembly");
            File.WriteAllText(ModAllowList.PathFor(mods),
                              $$"""{ "noclipmod.DLL": "{{HashOf(mod).ToLowerInvariant()}}" }""");

            ModAllowList? list = ModAllowList.Load(mods, out _);

            Assert.IsNotNull(list);
            Assert.IsTrue(list.IsAllowed(mod, out string reason), reason);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }
}
