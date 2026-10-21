using System.Reflection;
using System.Runtime.Loader;
using SensorPanel.Plugins;

namespace SensorPanel.Display;

/// <summary>A plugin DLL that was looked at, and what came of it.</summary>
public sealed record PluginInfo(string File, IReadOnlyList<string> DisplayTypes, string? Error);

/// <summary>
/// Loads display plugins: every DLL in the plugins folder and its subfolders (one folder per plugin keeps its
/// dependencies together). Each plugin gets its own load context; the plugin contract is shared with SensorPanel.
/// </summary>
public static class PluginLoader
{
    /// <summary>All display plugins live here, next to the program (App\plugins), one folder per plugin.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    public static (List<IDisplayDriver> Drivers, List<PluginInfo> Plugins) Load(string folder)
    {
        var drivers = new List<IDisplayDriver>();
        var plugins = new List<PluginInfo>();
        if (!Directory.Exists(folder)) return (drivers, plugins);

        foreach (string dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(dll);
            // The contract itself (copied next to a plugin) and plugins' own dependencies aren't plugins.
            if (name.Equals("SensorPanel.Plugins.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsManagedAssembly(dll)) continue;
            try
            {
                var context = new PluginContext(dll);
                var assembly = context.LoadFromAssemblyPath(dll);
                var found = new List<string>();
                foreach (var type in SafeTypes(assembly))
                {
                    if (type is not { IsClass: true, IsAbstract: false, IsPublic: true } ||
                        !typeof(IDisplayDriver).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                        continue;
                    var driver = (IDisplayDriver)Activator.CreateInstance(type)!;
                    if (drivers.Any(d => d.Id == driver.Id)) continue; // two plugins for the same display type: the first wins
                    drivers.Add(driver);
                    found.Add(driver.Name);
                }
                if (found.Count > 0) plugins.Add(new PluginInfo(dll, found, null));
            }
            catch (Exception ex)
            {
                plugins.Add(new PluginInfo(dll, [], ex.GetBaseException().Message));
            }
        }
        return (drivers, plugins);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false; // native DLL a plugin uses
        }
    }

    /// <summary>Resolves a plugin's own dependencies from its folder; shares the contract with SensorPanel.</summary>
    private sealed class PluginContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath))
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "SensorPanel.Plugins") return null; // use SensorPanel's copy, so the interfaces match
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
