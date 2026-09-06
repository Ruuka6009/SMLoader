using System.Reflection;
using System.Runtime.Loader;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Discovers and instantiates mods from <c>&lt;root&gt;/Mods/&lt;name&gt;/*.dll</c>.
/// </summary>
internal sealed class ModLoader
{
    private readonly string _root;
    private readonly List<IMod> _loaded = new();

    public ModLoader(string root) => _root = root;

    public IReadOnlyList<IMod> Loaded => _loaded;

    public void LoadAll()
    {
        string modsDirectory = Path.Combine(_root, "Mods");
        if (!Directory.Exists(modsDirectory))
        {
            Logging.Write($"no Mods directory at {modsDirectory}; nothing to load");
            return;
        }

        foreach (string modDirectory in Directory.EnumerateDirectories(modsDirectory))
            LoadFrom(modDirectory);

        Logging.Write($"{_loaded.Count} mod(s) loaded");
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
