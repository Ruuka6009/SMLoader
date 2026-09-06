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

        foreach (string modDirectory in Directory.EnumerateDirectories(modsDirectory))
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
                var host = new ModHost(_root, mod.Name);

                mod.OnLoad(host);
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
        using var image = new MemoryStream(File.ReadAllBytes(assemblyPath));

        string symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(symbolsPath))
            return context.LoadFromStream(image);

        // Carrying the pdb keeps line numbers in mod stack traces.
        using var symbols = new MemoryStream(File.ReadAllBytes(symbolsPath));
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
