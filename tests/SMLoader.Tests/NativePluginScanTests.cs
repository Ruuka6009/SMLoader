using SMLoader.Core;

namespace SMLoader.Tests;

[TestClass]
public class NativePluginScanTests
{
    /// <summary>A folder of its own per test, because these all touch the disk.</summary>
    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "smloader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ManagedAssembly => typeof(NativePluginScanTests).Assembly.Location;

    private static string NativeX64Dll => Path.Combine(Environment.SystemDirectory, "kernel32.dll");

    private static void Write(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- Classify ----------------------------------------------------------

    [TestMethod]
    public void AManagedAssemblyIsRecognisedAsManaged()
        => Assert.AreEqual(ImageKind.Managed, NativePluginScan.Classify(ManagedAssembly));

    [TestMethod]
    public void ANativeX64DllIsRecognisedAsNative()
        => Assert.AreEqual(ImageKind.Native, NativePluginScan.Classify(NativeX64Dll));

    [TestMethod]
    public void A32BitDllIsNotOfferedToA64BitProcess()
    {
        // SysWOW64 holds the 32-bit system DLLs on every x64 Windows install.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                   "SysWOW64", "kernel32.dll");
        if (!File.Exists(path))
            Assert.Inconclusive("no 32-bit system DLL on this machine");

        Assert.AreEqual(ImageKind.NativeWrongBitness, NativePluginScan.Classify(path));
    }

    [TestMethod]
    public void SomethingThatIsNotAPeImageIsNotMistakenForOne()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "notreally.dll");
            Write(path, "this is a text file wearing a .dll extension");

            Assert.AreEqual(ImageKind.NotAnImage, NativePluginScan.Classify(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Entry DLL selection ----------------------------------------------

    [TestMethod]
    public void TheDllNamedAfterTheFolderWins()
    {
        string directory = NewDirectory();
        try
        {
            string mod = Path.Combine(directory, "MyMod");
            Write(Path.Combine(mod, "MyMod.dll"));
            Write(Path.Combine(mod, "helper.dll"));

            string? entry = NativePluginScan.SelectEntryDll(mod, _ => Assert.Fail("should not have complained"));

            Assert.AreEqual("MyMod.dll", Path.GetFileName(entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ASingleDllIsTheEntryWhateverItIsCalled()
    {
        // The ReShade case: the folder is called ReShade and the DLL is dxgi.dll.
        string directory = NewDirectory();
        try
        {
            string mod = Path.Combine(directory, "ReShade");
            Write(Path.Combine(mod, "dxgi.dll"));

            string? entry = NativePluginScan.SelectEntryDll(mod, _ => Assert.Fail("should not have complained"));

            Assert.AreEqual("dxgi.dll", Path.GetFileName(entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ACopyOfTheApiNextToAModIsNotMistakenForIt()
    {
        string directory = NewDirectory();
        try
        {
            string mod = Path.Combine(directory, "ReShade");
            Write(Path.Combine(mod, "dxgi.dll"));
            Write(Path.Combine(mod, "SMLoader.Api.dll"));

            string? entry = NativePluginScan.SelectEntryDll(mod, _ => Assert.Fail("should not have complained"));

            Assert.AreEqual("dxgi.dll", Path.GetFileName(entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void SeveralDllsAndNoObviousEntryIsReportedRatherThanGuessed()
    {
        string directory = NewDirectory();
        try
        {
            string mod = Path.Combine(directory, "Ambiguous");
            Write(Path.Combine(mod, "one.dll"));
            Write(Path.Combine(mod, "two.dll"));

            var notes = new List<string>();
            string? entry = NativePluginScan.SelectEntryDll(mod, notes.Add);

            Assert.IsNull(entry);
            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains(notes[0], "Ambiguous.dll");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AVersionResourceNamingTheModBreaksTheTie()
    {
        // kernel32's product name is not "ReShade", so the real payoff cannot be
        // reproduced without shipping a binary. What can be pinned down is the
        // rule underneath it: when several DLLs sit together, the one whose
        // version resource names the folder is the one chosen.
        string directory = NewDirectory();
        try
        {
            // Read from the copy, not from System32. A system DLL's product
            // name comes out of its MUI companion and is localised; the copy
            // has no MUI beside it and falls back to the embedded string, so
            // the two do not agree on a non-English Windows.
            string staged = Path.Combine(directory, "renamed.dll");
            File.Copy(NativeX64Dll, staged);

            string product =
                System.Diagnostics.FileVersionInfo.GetVersionInfo(staged).ProductName?.Trim() ?? "";
            if (product.Length == 0)
                Assert.Inconclusive("kernel32.dll carries no product name on this machine");

            string mod = Path.Combine(directory, product);
            Directory.CreateDirectory(mod);
            File.Move(staged, Path.Combine(mod, "renamed.dll"));
            Write(Path.Combine(mod, "other.dll"));

            string? entry = NativePluginScan.SelectEntryDll(mod, _ => Assert.Fail("should not have complained"));

            Assert.AreEqual("renamed.dll", Path.GetFileName(entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Discover ----------------------------------------------------------

    [TestMethod]
    public void OnlyTheNativeFoldersAreOfferedToTheShim()
    {
        string mods = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(mods, "NoclipMod"));
            File.Copy(ManagedAssembly, Path.Combine(mods, "NoclipMod", "NoclipMod.dll"));

            Directory.CreateDirectory(Path.Combine(mods, "ReShade"));
            File.Copy(NativeX64Dll, Path.Combine(mods, "ReShade", "dxgi.dll"));

            List<NativePluginInfo> found = NativePluginScan.Discover(mods, _ => { });

            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("dxgi.dll", Path.GetFileName(found[0].Path));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(mods, "ReShade")),
                            found[0].Directory, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void AddonsSittingNextToThePluginAreCounted()
    {
        string mods = NewDirectory();
        try
        {
            string plugin = Path.Combine(mods, "ReShade");
            Directory.CreateDirectory(plugin);
            File.Copy(NativeX64Dll, Path.Combine(plugin, "dxgi.dll"));
            Write(Path.Combine(plugin, "AutoHDR.addon64"));
            Write(Path.Combine(plugin, "ShaderToggler.addon"));
            Write(Path.Combine(plugin, "ReShade.ini"));
            Write(Path.Combine(plugin, "notanaddon.addon32"));

            List<NativePluginInfo> found = NativePluginScan.Discover(mods, _ => { });

            Assert.AreEqual(1, found.Count);
            Assert.AreEqual(2, found[0].AddonCount);
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [TestMethod]
    public void AMissingModsDirectoryIsNotAnError()
        => Assert.AreEqual(0, NativePluginScan.Discover(
               Path.Combine(Path.GetTempPath(), "smloader-tests-" + Guid.NewGuid().ToString("N")),
               _ => Assert.Fail("should not have complained")).Count);

    // ---- Describe ----------------------------------------------------------

    [TestMethod]
    public void DescribeNamesTheProductAndVersion()
        => Assert.AreEqual("ReShade 6.8.0",
                           new NativePluginInfo(@"C:\x", @"C:\x\dxgi.dll", "ReShade", "6.8.0", 0).Describe());

    [TestMethod]
    public void DescribeCountsAddonsAndAgreesWithItselfAboutThePlural()
    {
        Assert.AreEqual("ReShade 6.8.0 (+1 add-on)",
                        new NativePluginInfo(@"C:\x", @"C:\x\dxgi.dll", "ReShade", "6.8.0", 1).Describe());
        Assert.AreEqual("ReShade 6.8.0 (+22 add-ons)",
                        new NativePluginInfo(@"C:\x", @"C:\x\dxgi.dll", "ReShade", "6.8.0", 22).Describe());
    }

    [TestMethod]
    public void DescribeLeavesOutAVersionItDoesNotHave()
        => Assert.AreEqual("mystery.dll",
                           new NativePluginInfo(@"C:\x", @"C:\x\mystery.dll", "mystery.dll", "", 0).Describe());

    [TestMethod]
    public void ReShadeIsRecognisedFromItsProductNameHoweverTheDllIsCalled()
    {
        Assert.IsTrue(new NativePluginInfo(@"C:\x", @"C:\x\dxgi.dll", "ReShade", "6.8.0", 0).IsReShade);
        Assert.IsFalse(new NativePluginInfo(@"C:\x", @"C:\x\enb.dll", "ENBSeries", "0.4", 0).IsReShade);
    }
}
