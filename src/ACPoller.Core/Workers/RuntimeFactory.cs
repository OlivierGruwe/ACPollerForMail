using System.Text;
using System.Text.Json;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Sources;
using ACPoller.Core.Configuration;
using ACPoller.Core.Pipeline;
using ACPoller.Core.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ACPoller.Core.Workers;

/// <summary>
/// Instancie la source et les composants du pipeline pour une configuration.
/// </summary>
/// <remarks>
/// Les fabriques du contrat attendent une <c>IConfiguration</c>, alors que le
/// modele d'options est fortement type. Le pont se fait par serialisation JSON
/// puis relecture : c'est generique, ca survit a l'ajout d'un champ, et ca evite
/// d'imposer une signature par transport dans le contrat public.
/// </remarks>
public sealed class RuntimeFactory(
    IServiceProvider services,
    PluginManager plugins,
    IEnumerable<IMailSourceFactory> sourceFactories,
    IEnumerable<IExportTargetFactory> exportFactories,
    IEnumerable<IMetadataWriter> metadataWriters)
{
    private readonly IMailSourceFactory[] _sourceFactories = [.. sourceFactories];
    private readonly IExportTargetFactory[] _exportFactories = [.. exportFactories];
    private readonly IMetadataWriter[] _metadataWriters = [.. metadataWriters];

    /// <summary>Cree la source de messages declaree par la configuration.</summary>
    /// <param name="configuration">Configuration resolue.</param>
    /// <returns>La source, a liberer par l'appelant.</returns>
    public IMailSource CreateSource(PollerConfiguration configuration)
    {
        var factory = _sourceFactories.FirstOrDefault(f =>
            f.ProtocolType.Equals(configuration.Source.Protocol, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Aucune source pour le protocole '{configuration.Source.Protocol}'.");

        return factory.Create(services, ToConfiguration(configuration.Source));
    }

    /// <summary>Assemble les composants du pipeline pour cette configuration.</summary>
    /// <param name="configuration">Configuration resolue.</param>
    /// <returns>Les composants, dont les cibles d'export a liberer par l'appelant.</returns>
    public PipelineComponents CreateComponents(PollerConfiguration configuration)
    {
        // Les processeurs viennent des plugins et sont filtres par la
        // configuration : un plugin charge n'est pas actif partout.
        var processors = plugins.Current.Processors
            .Where(p => configuration.Processors.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.Order)
            .ToArray();

        var targets = new List<(IExportTarget Target, PolicyOptions Policy)>();

        foreach (var options in configuration.Output.Targets)
        {
            var factory = _exportFactories.FirstOrDefault(f =>
                f.TargetType.Equals(options.Type, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Configuration '{configuration.Name}' : aucune cible pour le type '{options.Type}'.");

            var target = factory.Create(services, ToConfiguration(options.Settings));
            targets.Add((target, options.Policy ?? configuration.Output.Policy));
        }

        var writer = _metadataWriters.FirstOrDefault(w =>
            w.Format.Equals(configuration.Output.Metadata.Format, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Configuration '{configuration.Name}' : aucun writer pour le format "
                + $"'{configuration.Output.Metadata.Format}'.");

        return new PipelineComponents(processors, targets, writer);
    }

    private static IConfiguration ToConfiguration<T>(T options)
    {
        var json = JsonSerializer.Serialize(options);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }
}

/// <summary>Extensions d'enregistrement du socle dans le conteneur.</summary>
public static class RuntimeFactoryExtensions
{
    /// <summary>Enregistre la fabrique d'execution.</summary>
    /// <param name="services">Conteneur.</param>
    /// <returns>Le conteneur, pour le chainage.</returns>
    public static IServiceCollection AddRuntimeFactory(this IServiceCollection services) =>
        services.AddSingleton<RuntimeFactory>();
}
