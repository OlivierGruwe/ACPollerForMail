using System.Reflection;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Plugins;
using ACPoller.Abstractions.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Plugins;

/// <summary>
/// Ensemble des plugins actuellement charges, avec leur conteneur et leur
/// contexte de chargement. Immuable : un rechargement produit une nouvelle
/// instance, il ne modifie pas celle-ci.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly PluginLoadContext[] _contexts;

    internal PluginHost(
        ServiceProvider services,
        PluginLoadContext[] contexts,
        IReadOnlyList<PluginDescriptor> descriptors)
    {
        Services = services;
        _contexts = contexts;
        Descriptors = descriptors;
    }

    public ServiceProvider Services { get; }

    public IReadOnlyList<PluginDescriptor> Descriptors { get; }

    public IReadOnlyList<ICaptureProcessor> Processors =>
        [.. Services.GetServices<ICaptureProcessor>()];

    public IReadOnlyList<IExportTargetFactory> ExportFactories =>
        [.. Services.GetServices<IExportTargetFactory>()];

    public IReadOnlyList<IDocumentConverter> Converters =>
        [.. Services.GetServices<IDocumentConverter>()];

    public void Dispose()
    {
        Services.Dispose();

        foreach (var context in _contexts)
        {
            context.Unload();
        }

        // Le dechargement effectif n'a lieu qu'apres collecte, et UNIQUEMENT si
        // plus aucune reference ne subsiste vers un type du plugin. Un traitement
        // encore en vol suffit a le bloquer : d'ou l'obligation, pour l'appelant,
        // d'avoir mis les workers en pause avant de recharger.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>
/// Charge les plugins compatibles et expose l'hote courant.
/// Le rechargement a chaud suppose que l'appelant a suspendu les workers :
/// voir <see cref="PluginHost.Dispose"/>.
/// </summary>
public sealed class PluginManager(
    PluginDiscovery discovery,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<PluginManager> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private PluginHost? _current;

    public PluginHost Current => _current
        ?? throw new InvalidOperationException("Aucun hote de plugins charge. Appeler Load au demarrage.");

    /// <summary>Noms des processeurs disponibles, pour la validation de configuration.</summary>
    public IReadOnlyCollection<string> KnownProcessors =>
        [.. Current.Processors.Select(p => p.Name)];

    /// <summary>Types de cibles disponibles, socle et plugins confondus.</summary>
    public IReadOnlyCollection<string> KnownTargetTypes =>
        [.. Current.ExportFactories.Select(f => f.TargetType)];

    public PluginHost Load(string? pluginDirectory)
    {
        lock (_gate)
        {
            var descriptors = string.IsNullOrWhiteSpace(pluginDirectory)
                ? []
                : discovery.Discover(pluginDirectory);

            var services = new ServiceCollection();
            services.AddSingleton(loggerFactory);
            services.AddLogging();

            var contexts = new List<PluginLoadContext>();

            foreach (var descriptor in descriptors)
            {
                if (!descriptor.IsCompatible)
                {
                    // Refus explicite et trace, jamais un chargement optimiste.
                    logger.LogError(
                        "Plugin {Name} refuse : {Reason}",
                        descriptor.Name,
                        descriptor.IncompatibilityReason);
                    continue;
                }

                try
                {
                    var context = new PluginLoadContext(descriptor.AssemblyPath);
                    var assembly = context.LoadFromAssemblyPath(descriptor.AssemblyPath);

                    var attribute = assembly.GetCustomAttribute<AcPollerPluginAttribute>()
                        ?? throw new InvalidOperationException("Attribut de plugin introuvable apres chargement.");

                    var plugin = (IAcPollerPlugin)Activator.CreateInstance(attribute.EntryPoint)!;
                    plugin.ConfigureServices(services, configuration);

                    contexts.Add(context);
                    logger.LogInformation("Plugin {Name} {Version} charge", plugin.Name, plugin.Version);
                }
                catch (Exception ex)
                {
                    // Un plugin defaillant ne doit jamais empecher le service de
                    // demarrer : les autres flux continuent, celui-ci est signale.
                    logger.LogError(ex, "Chargement du plugin {Name} echoue", descriptor.Name);
                }
            }

            var previous = _current;
            _current = new PluginHost(services.BuildServiceProvider(), [.. contexts], descriptors);
            previous?.Dispose();

            return _current;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }
}
