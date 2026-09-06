using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Discovers and instantiates mods from <c>&lt;root&gt;/Mods/&lt;name&gt;/*.dll</c>.
/// </summary>
internal sealed class ModLoader
{
    private readonly string _root;
    private readonly List<IMod> _loaded = new();

    /// <summary>File name -> expected SHA-256, or null when no allowlist is in force.</summary>
    private Dictionary<string, string>? _allowed;

    /// <summary>Names already taken, so a duplicate is caught rather than shadowing.</summary>
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One per loaded mod, kept so a pending config write can be flushed at exit.</summary>
    private readonly List<ModHost> _hosts = new();

    public ModLoader(string root) => _root = root;

    public IReadOnlyList<IMod> Loaded => _loaded;

    public void LoadAll()
    {
        // Safe mode. The point of it is to answer "is this SMLoader or is this a
        // mod?" in one launch, without the player having to move folders around.
        if (Environment.GetEnvironmentVariable("SMLOADER_NO_MODS") == "1")
        {
            Logging.Write("safe mode (--no-mods): no mods will be loaded");
            return;
        }

        string modsDirectory = Path.Combine(_root, "Mods");
        if (!Directory.Exists(modsDirectory))
        {
            Logging.Write($"no Mods directory at {modsDirectory}; nothing to load");
            return;
        }

        LoadAllowList(modsDirectory);

        // Sorted, because EnumerateDirectories returns filesystem order, which is
        // neither stable nor meaningful. Two mods appending to the same script and
        // both wrapping client_onUpdate - which SettingsPanel and NoclipMod do
        // today - would otherwise nest in whichever order the volume happened to
        // hand back. Ordinal by directory name is arbitrary but at least the same
        // on every machine and every launch, until mod.json declares it properly
        // (OPTIMISATION.md 8.2).
        List<string> directories = Directory.EnumerateDirectories(modsDirectory).ToList();
        directories.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string modDirectory in directories)
            LoadFrom(modDirectory);

        Logging.Write($"{_loaded.Count} mod(s) loaded");

        if (_loaded.Count > 0)
        {
            // Said at every launch, not just the first: a mod added last week is
            // still native code running with this user's privileges today.
            Logging.Write("mods run as native code inside ScrapMechanic.exe with your " +
                          "account's full privileges; SMLoader does not sandbox them");
        }
    }

    /// <summary>
    /// Optional SHA-256 allowlist at <c>Mods/allowed.json</c>, shaped
    /// <c>{ "NoclipMod.dll": "&lt;hex&gt;" }</c>. Absent means every mod loads,
    /// which is the default and matches what the loader has always done.
    /// </summary>
    private void LoadAllowList(string modsDirectory)
    {
        if (Environment.GetEnvironmentVariable("SMLOADER_ANY_MOD") == "1")
        {
            Logging.Write("--any-mod: the allowlist is ignored for this launch");
            return;
        }

        string path = Path.Combine(modsDirectory, "allowed.json");
        if (!File.Exists(path))
            return;

        try
        {
            _allowed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            Logging.Write($"allowlist active: {_allowed?.Count ?? 0} entr(ies) from {path}");
        }
        catch (Exception ex)
        {
            // Refuse everything rather than silently degrading to "load anything":
            // a corrupt allowlist must not be a way past the allowlist.
            _allowed = new Dictionary<string, string>();
            Logging.Error($"could not read {path}; no mod will be allowed to load", ex);
        }
    }

    /// <summary>
    /// True when the assembly may load: either no allowlist is in force, or its
    /// SHA-256 matches the entry for its file name.
    /// </summary>
    private bool IsAllowed(string assemblyPath)
    {
        if (_allowed is null)
            return true;

        string name = Path.GetFileName(assemblyPath);
        if (!_allowed.TryGetValue(name, out string? expected))
        {
            Logging.Error($"{name} is not in the allowlist; not loading it");
            return false;
        }

        string actual;
        try
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            Logging.Error($"could not hash {assemblyPath}; not loading it", ex);
            return false;
        }

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return true;

        Logging.Error($"{name} does not match the allowlist (expected {expected}, " +
                      $"found {actual}); not loading it");
        return false;
    }

    private void LoadFrom(string directory)
    {
        string name = Path.GetFileName(directory);
        string assemblyPath = Path.Combine(directory, name + ".dll");

        if (!File.Exists(assemblyPath))
        {
            // Fall back to the first assembly that is not a copy of the API.
            assemblyPath = Directory
                .EnumerateFiles(directory, "*.dll")
                .FirstOrDefault(p => !Path.GetFileName(p).StartsWith("SMLoader.", StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;

            if (assemblyPath.Length == 0)
            {
                Logging.Write($"skipping {name}: no candidate assembly");
                return;
            }
        }

        if (!IsAllowed(assemblyPath))
            return;

        Type[] types;
        try
        {
            var context = new ModLoadContext(assemblyPath);

            // Loaded from memory rather than by path: a running game would
            // otherwise hold the file open and block the next mod rebuild.
            Assembly assembly = LoadWithoutLocking(context, assemblyPath);

            if (!IsApiCompatible(assembly, name))
                return;

            types = GetLoadableTypes(assembly, name);
        }
        catch (Exception ex)
        {
            Logging.Error($"failed to load mod '{name}'", ex);
            return;
        }

        foreach (Type type in types)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IMod).IsAssignableFrom(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            // Per type, not per assembly: a throwing constructor in the first
            // IMod must not hide the ones after it.
            try
            {
                var mod = (IMod)Activator.CreateInstance(type)!;

                // Config files, settings keys and log prefixes are all keyed on
                // the name, so two mods sharing one would quietly overwrite each
                // other's settings.
                if (!_names.Add(mod.Name))
                {
                    Logging.Error($"a mod named '{mod.Name}' is already loaded; " +
                                  $"skipping the one in {Path.GetFileName(assemblyPath)}");
                    continue;
                }

                var host = new ModHost(_root, mod.Name);
                _hosts.Add(host);

                long started = Environment.TickCount64;
                mod.OnLoad(host);
                Metrics.ModLoadTime(Environment.TickCount64 - started);

                _loaded.Add(mod);
                Logging.Write($"loaded {mod.Name} {mod.Version} from {Path.GetFileName(assemblyPath)}");
            }
            catch (Exception ex)
            {
                Logging.Error($"failed to load '{type.FullName}' from mod '{name}'", ex);
            }
        }
    }

    /// <summary>
    /// Flushes anything a mod left pending. Called from the process-exit path,
    /// so every failure is swallowed: a throw here turns a clean exit into a
    /// crash report, and the settings are already the thing being lost.
    /// </summary>
    public void Shutdown()
    {
        foreach (ModHost host in _hosts)
        {
            try { host.OwnedConfig.Dispose(); }
            catch { /* exiting anyway */ }
        }
    }

    /// <summary>
    /// Checks the API version the mod was built against before anything in it
    /// is constructed. The alternative is a MissingMethodException at an
    /// arbitrary later moment, usually inside a Lua callback where the
    /// trampoline catches it and the player just sees a feature not working.
    /// </summary>
    private static bool IsApiCompatible(Assembly assembly, string name)
    {
        var stamp = assembly.GetCustomAttribute<SMLoaderApiVersionAttribute>();
        if (stamp is null)
        {
            // Built before the handshake existed, or outside this repository.
            // Loading it is no worse than the loader has always been, so this
            // warns rather than refuses - see OPTIMISATION.md 9.12.
            Logging.Warn($"{name} carries no API version; loading it anyway, but it " +
                         $"cannot be checked against SMLoader API {ApiVersion.Text}");
            return true;
        }

        if (ApiVersion.IsCompatible(stamp.Version, out string reason))
            return true;

        Logging.Error($"{name} {reason}; not loading it");
        return false;
    }

    /// <summary>
    /// A mod whose assembly references a type it cannot resolve still has a
    /// perfectly loadable IMod in it most of the time, so keep what did load
    /// instead of discarding the whole assembly.
    /// </summary>
    private static Type[] GetLoadableTypes(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Logging.Error($"{name}: some types failed to load", ex);
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    private static Assembly LoadWithoutLocking(AssemblyLoadContext context, string assemblyPath)
    {
        // writable: false wraps the array rather than copying it again.
        using var image = new MemoryStream(File.ReadAllBytes(assemblyPath), writable: false);

        string symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(symbolsPath))
            return context.LoadFromStream(image);

        // Carrying the pdb keeps line numbers in mod stack traces.
        using var symbols = new MemoryStream(File.ReadAllBytes(symbolsPath), writable: false);
        return context.LoadFromStream(image, symbols);
    }

    /// <summary>
    /// Gives each mod its own load context for private dependencies, while
    /// SMLoader's own assemblies keep coming from the default context so that
    /// <see cref="IMod"/> is the same type on both sides.
    /// </summary>
    private sealed class ModLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ModLoadContext(string assemblyPath)
            : base(name: Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
            => _resolver = new AssemblyDependencyResolver(assemblyPath);

        /// <summary>
        /// SMLoader.Core is hosted in its own component load context, not the
        /// default one, so returning null for SMLoader.* would send the runtime
        /// looking in the default context where SMLoader.Api does not exist.
        /// Hand back the already-loaded instances instead - that also keeps
        /// IMod the same type on both sides.
        /// </summary>
        private static readonly Assembly[] SharedAssemblies =
        {
            typeof(IMod).Assembly,
            typeof(ModLoader).Assembly,
        };

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } requested &&
                requested.StartsWith("SMLoader.", StringComparison.OrdinalIgnoreCase))
            {
                return SharedAssemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, requested, StringComparison.OrdinalIgnoreCase));
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
