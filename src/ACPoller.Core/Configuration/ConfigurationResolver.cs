using ACPoller.Abstractions.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Configuration;

/// <summary>
/// Fusionne chaque configuration avec son gabarit et dechiffre les secrets.
/// </summary>
/// <remarks>
/// La fusion s'appuie sur le comportement du binder : <c>Bind</c> n'ecrit que
/// les proprietes effectivement presentes dans la section. On lie donc le
/// gabarit puis la configuration sur LA MEME instance, et seuls les ecarts
/// declares ecrasent l'heritage. Pas de moteur de merge maison a maintenir.
///
/// Piege connu, traite explicitement plus bas : pour une liste, <c>Bind</c>
/// fusionne PAR INDICE au lieu de remplacer. Un gabarit avec trois cibles et
/// une configuration qui en declare une seule produirait trois cibles, la
/// premiere ecrasee. Les listes sont donc videes avant liaison des lors que la
/// configuration les redeclare.
/// </remarks>
public sealed class ConfigurationResolver(ISecretProtector secretProtector, ILogger<ConfigurationResolver> logger)
{
    private static readonly string[] ListPaths =
    [
        "Output:Targets",
        "Processors",
        "Conversion:ExcludedExtensions",
    ];

    /// <summary>
    /// Noms des gabarits declares. Le validateur en a besoin pour signaler un
    /// gabarit introuvable, que le resolveur se contente d'ecarter.
    /// </summary>
    /// <param name="root">Configuration racine.</param>
    /// <returns>Les noms de gabarits.</returns>
    public static IReadOnlyCollection<string> GetTemplateNames(IConfiguration root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return
        [
            .. root.GetSection(PollerOptions.SectionName)
                .GetSection(nameof(PollerOptions.Templates))
                .GetChildren()
                .Select(s => s.Key)
        ];
    }

    /// <summary>Resout toutes les configurations declarees.</summary>
    /// <param name="root">Configuration racine.</param>
    /// <returns>Les configurations resolues, celles en erreur etant ecartees.</returns>
    public IReadOnlyList<PollerConfiguration> Resolve(IConfiguration root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var section = root.GetSection(PollerOptions.SectionName);
        var templatesSection = section.GetSection(nameof(PollerOptions.Templates));
        var configurationsSection = section.GetSection(nameof(PollerOptions.Configurations));

        var resolved = new List<PollerConfiguration>();

        foreach (var configSection in configurationsSection.GetChildren())
        {
            var target = new PollerConfiguration();

            var configName = configSection[nameof(PollerConfiguration.Name)];
            var templateName = configSection[nameof(PollerConfiguration.Template)];

            if (!string.IsNullOrWhiteSpace(templateName))
            {
                var templateSection = templatesSection.GetSection(templateName);

                if (!templateSection.Exists())
                {
                    // Ecarter et non lever : Resolve est appele au demarrage du
                    // service, et une faute de frappe dans une configuration
                    // empecherait alors les quatre-vingt-dix-neuf autres boites
                    // de demarrer. Le validateur produit l'anomalie bloquante
                    // correspondante, que l'interface affiche.
                    logger.LogError(
                        "Configuration {Name} ecartee : gabarit {Template} introuvable.",
                        configName,
                        templateName);

                    continue;
                }

                templateSection.Bind(target);
            }

            // Une liste redeclaree remplace celle du gabarit au lieu de fusionner
            // par indice. Le vidage doit avoir lieu APRES la liaison du gabarit
            // et AVANT celle de la configuration.
            ClearRedeclaredLists(configSection, target);

            configSection.Bind(target);

            // Le nom appartient toujours a la configuration, jamais au gabarit :
            // deux boites heritant du meme gabarit porteraient sinon le meme nom.
            if (string.IsNullOrWhiteSpace(configName))
            {
                // Meme principe : une entree sans nom est ecartee, pas fatale.
                logger.LogError("Configuration sans Name ecartee.");
                continue;
            }

            target.Name = configName;

            UnprotectSecrets(target);

            resolved.Add(target);
            logger.LogDebug(
                "Configuration {Name} resolue (gabarit : {Template})",
                target.Name,
                templateName ?? "aucun");
        }

        return resolved;
    }

    private static void ClearRedeclaredLists(IConfigurationSection configSection, PollerConfiguration target)
    {
        foreach (var path in ListPaths)
        {
            if (!configSection.GetSection(path).Exists())
            {
                continue;
            }

            switch (path)
            {
                case "Output:Targets":
                    target.Output.Targets.Clear();
                    break;
                case "Processors":
                    target.Processors.Clear();
                    break;
                case "Conversion:ExcludedExtensions":
                    target.Conversion.ExcludedExtensions.Clear();
                    break;
            }
        }
    }

    /// <summary>
    /// Dechiffre les valeurs protegees. Les secrets restent chiffres sur disque
    /// et ne vivent en clair qu'en memoire, le temps de l'execution.
    /// </summary>
    private void UnprotectSecrets(PollerConfiguration configuration)
    {
        var source = configuration.Source;

        source.ClientSecret = Unprotect(source.ClientSecret);
        source.Password = Unprotect(source.Password);

        foreach (var target in configuration.Output.Targets)
        {
            foreach (var key in target.Settings.Keys.ToList())
            {
                if (secretProtector.IsProtected(target.Settings[key]))
                {
                    target.Settings[key] = secretProtector.Unprotect(target.Settings[key]);
                }
            }
        }
    }

    private string? Unprotect(string? value) =>
        string.IsNullOrEmpty(value) || !secretProtector.IsProtected(value)
            ? value
            : secretProtector.Unprotect(value);
}
