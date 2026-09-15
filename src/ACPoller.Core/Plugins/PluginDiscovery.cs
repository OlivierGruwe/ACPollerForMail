using System.Reflection;
using System.Runtime.InteropServices;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Plugins;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Plugins;

/// <summary>
/// Inventorie les plugins d'un repertoire SANS executer leur code.
/// </summary>
/// <remarks>
/// L'inspection passe par un <c>MetadataLoadContext</c> : les assemblages sont
/// lus comme des donnees, aucun constructeur statique n'est declenche, aucun
/// code du plugin ne s'execute. Un assemblage corrompu, compile contre une
/// version de contrat obsolete ou simplement etranger au repertoire est donc
/// ecarte avec un message lisible, au lieu de faire tomber le service au
/// premier appel.
///
/// C'est la difference de fond avec la v1, ou une signature <c>applyAssembly</c>
/// incorrecte ne se manifestait qu'a l'execution, sur la premiere facture.
/// </remarks>
public sealed class PluginDiscovery(ILogger<PluginDiscovery> logger)
{
    public IReadOnlyList<PluginDescriptor> Discover(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
        {
            logger.LogInformation("Repertoire de plugins absent : {Directory}", pluginDirectory);
            return [];
        }

        var candidates = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories);
        var descriptors = new List<PluginDescriptor>();

        // Les assemblages du runtime doivent etre resolvables, sinon la lecture
        // des attributs echoue des qu'un type du framework est reference.
        var runtimeAssemblies = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
        var contractAssembly = typeof(IAcPollerPlugin).Assembly.Location;

        var resolver = new PathAssemblyResolver(
            runtimeAssemblies
                .Concat(candidates)
                .Append(contractAssembly)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        using var metadataContext = new MetadataLoadContext(resolver);

        foreach (var candidate in candidates)
        {
            var descriptor = Inspect(metadataContext, candidate);
            if (descriptor is not null)
            {
                descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    private PluginDescriptor? Inspect(MetadataLoadContext context, string path)
    {
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);

            var attribute = assembly.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.FullName == typeof(AcPollerPluginAttribute).FullName);

            if (attribute is null)
            {
                // Ce n'est pas un plugin, c'est une dependance. Pas un incident.
                return null;
            }

            var contractVersion = (int)attribute.ConstructorArguments[0].Value!;
            var entryPointType = attribute.ConstructorArguments[1].Value as Type;

            var compatible = ContractVersion.Supported.Contains(contractVersion);

            return new PluginDescriptor
            {
                Name = entryPointType?.Name ?? assembly.GetName().Name ?? Path.GetFileName(path),
                Version = assembly.GetName().Version?.ToString() ?? "0.0.0",
                AssemblyPath = path,
                ContractVersion = contractVersion,
                IsCompatible = compatible,
                IncompatibilityReason = compatible
                    ? null
                    : $"Compile contre la version de contrat {contractVersion}, ce socle accepte "
                      + $"{string.Join(", ", ContractVersion.Supported)}. Recompiler le plugin.",
                ProvidedCapabilities = [],
            };
        }
        catch (BadImageFormatException)
        {
            // Assemblage natif ou illisible : ignorer sans bruit.
            return null;
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or TypeLoadException)
        {
            logger.LogWarning(ex, "Inspection impossible pour {Path}", path);

            return new PluginDescriptor
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Version = "inconnue",
                AssemblyPath = path,
                ContractVersion = 0,
                IsCompatible = false,
                IncompatibilityReason = $"Inspection impossible : {ex.Message}",
            };
        }
    }
}
