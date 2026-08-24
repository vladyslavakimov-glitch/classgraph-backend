using System.Reflection;
using System.Runtime.Loader;
using ClassGraph.SampleDomain;

namespace ClassGraph.Server;

public sealed class AnalysisAssembly : IDisposable
{
    private readonly AssemblyLoadContext? _loadContext;

    private AnalysisAssembly(Assembly assembly, string displayPath, AssemblyLoadContext? loadContext)
    {
        Assembly = assembly;
        DisplayPath = displayPath;
        _loadContext = loadContext;
    }

    public Assembly Assembly { get; }

    public string DisplayPath { get; }

    public static AnalysisAssembly Load(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var assembly = typeof(Person).Assembly;
            return new AnalysisAssembly(assembly, "Integrierte Demo-Assembly", null);
        }

        var fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Die Analyse-DLL wurde nicht gefunden: {fullPath}", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Die Analyse-Datei muss eine kompilierte .NET-DLL sein.");
        }

        try
        {
            var context = new PluginLoadContext(fullPath);
            var assembly = context.LoadFromAssemblyPath(fullPath);
            _ = assembly.GetExportedTypes();
            return new AnalysisAssembly(assembly, fullPath, context);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
        {
            throw new InvalidOperationException(
                $"Die DLL '{fullPath}' konnte nicht als kompatible .NET-Assembly geladen werden: {exception.Message}",
                exception);
        }
    }

    public void Dispose()
    {
        if (_loadContext?.IsCollectible == true)
        {
            _loadContext.Unload();
        }
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
