using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ACPoller.Abstractions.Plugins;

/// <summary>
/// Marque un assemblage comme plugin ACPoller et declare la version de contrat
/// contre laquelle il a ete compile. Le loader lit cet attribut par metadonnees
/// avant toute instanciation : un plugin incompatible est refuse proprement,
/// avec un message explicite, au lieu de lever une MissingMethodException.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class AcPollerPluginAttribute : Attribute
{
    /// <summary>Declare l'assemblage comme plugin.</summary>
    /// <param name="contractVersion">Version de contrat de compilation.</param>
    /// <param name="entryPoint">Type implementant <see cref="IAcPollerPlugin"/>.</param>
    public AcPollerPluginAttribute(int contractVersion, Type entryPoint)
    {
        ContractVersion = contractVersion;
        EntryPoint = entryPoint;
    }

    /// <summary>Version de contrat contre laquelle le plugin a ete compile.</summary>
    public int ContractVersion { get; }

    /// <summary>Type implementant <see cref="IAcPollerPlugin"/>.</summary>
    public Type EntryPoint { get; }
}

/// <summary>
/// Point d'entree d'un plugin. Le plugin enregistre ses composants dans le
/// conteneur fourni ; le core ne connait que les interfaces.
/// Chargement dans un AssemblyLoadContext isole et dechargeable : une mise a jour
/// de plugin ne demande pas de redemarrer le service.
/// </summary>
public interface IAcPollerPlugin
{
    /// <summary>Nom du plugin, affiche dans l'UI et les journaux.</summary>
    string Name { get; }

    /// <summary>Version du plugin, affichee dans l'UI.</summary>
    string Version { get; }

    /// <summary>
    /// Enregistre convertisseurs, cibles d'export, processeurs metier et writers.
    /// Aucune I/O ici : la configuration est lue, pas appliquee.
    /// </summary>
    /// <param name="services">Conteneur dans lequel enregistrer les composants.</param>
    /// <param name="configuration">Configuration de l'application.</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}

/// <summary>Description d'un plugin decouvert, exposee a l'UI de parametrage.</summary>
public sealed record PluginDescriptor
{
    /// <summary>Nom du plugin.</summary>
    public required string Name { get; init; }

    /// <summary>Version de l'assemblage.</summary>
    public required string Version { get; init; }

    /// <summary>Chemin de l'assemblage sur disque.</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>Version de contrat declaree par le plugin.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>Vrai si le socle accepte cette version de contrat.</summary>
    public bool IsCompatible { get; init; }

    /// <summary>Motif de refus, affichable tel quel dans l'UI. Null si compatible.</summary>
    public string? IncompatibilityReason { get; init; }

    /// <summary>Composants fournis par le plugin, pour affichage.</summary>
    public IReadOnlyList<string> ProvidedCapabilities { get; init; } = [];
}
