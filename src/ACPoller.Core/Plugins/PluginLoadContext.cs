using System.Reflection;
using System.Runtime.Loader;

namespace ACPoller.Core.Plugins;

/// <summary>
/// Contexte de chargement dedie a un plugin, isole et dechargeable.
/// </summary>
/// <remarks>
/// Point critique : le contrat et les abstractions du framework doivent venir
/// du contexte PAR DEFAUT, jamais d'une copie locale au plugin. Deux copies de
/// <c>ACPoller.Abstractions</c> chargees dans deux contextes produisent deux
/// types <c>ICaptureProcessor</c> DIFFERENTS aux yeux du runtime, et le cast
/// echoue avec un message absurde du genre "impossible de convertir
/// ICaptureProcessor en ICaptureProcessor". C'est le piege classique de
/// l'AssemblyLoadContext, et la raison du <c>Private=false</c> pose sur la
/// reference du projet plugin.
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "ACPoller.Abstractions",
        "Microsoft.Extensions.",
        "System.",
        "netstandard",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        // null delegue au contexte par defaut : identite de type preservee.
        if (SharedPrefixes.Any(prefix => assemblyName.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
