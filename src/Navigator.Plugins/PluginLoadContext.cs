using System.Reflection;
using System.Runtime.Loader;

namespace Navigator.Plugins;

/// <summary>
/// Isolates one plugin's private dependencies (e.g. an SSH library) from the app and from other plugins.
/// Assemblies the host already has — Navigator.*, the plugin contract, the BCL and shared Microsoft.Extensions
/// libraries — are always taken from the default context, so contract types are identical on both sides and are
/// loaded once. Everything else is resolved from the plugin's folder (its .deps.json when present).
/// Not collectible: unloading would cost a slower context and gives little (removal finishes on restart).
/// </summary>
internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
    private readonly string _directory = Path.GetDirectoryName(mainAssemblyPath)!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName))
            return null; // default context

        if (_resolver.ResolveAssemblyToPath(assemblyName) is { } resolved)
            return LoadFromAssemblyPath(resolved);

        // Packages without a .deps.json: look next to the main assembly.
        var local = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        => _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    /// <summary>True when the default context provides the assembly in a version at least as new as requested.</summary>
    private static bool IsShared(AssemblyName name)
    {
        if (name.Name is null)
            return false;
        if (name.Name.StartsWith("Navigator.", StringComparison.Ordinal) && !name.Name.StartsWith("Navigator.Plugin.", StringComparison.Ordinal))
            return true;

        foreach (var loaded in Default.Assemblies)
        {
            var candidate = loaded.GetName();
            if (string.Equals(candidate.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return name.Version is null || candidate.Version is null || candidate.Version >= name.Version;
        }

        // Framework assemblies that are not loaded yet are still provided by the default context.
        return name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name is "System" or "mscorlib" or "netstandard";
    }
}
