using System.ComponentModel.DataAnnotations;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Sources;
using ACPoller.Core.Plugins;

namespace ACPoller.Core.Configuration;

/// <summary>Anomalie de configuration, formulee pour etre affichable telle quelle dans l'UI.</summary>
/// <param name="ConfigurationName">Configuration concernee.</param>
/// <param name="Path">Chemin du reglage fautif, au format section:propriete.</param>
/// <param name="Message">Message affichable tel quel.</param>
/// <param name="IsBlocking">Vrai si la configuration doit etre ecartee, faux pour un simple avertissement.</param>
public sealed record ConfigurationIssue(string ConfigurationName, string Path, string Message, bool IsBlocking);

/// <summary>
/// Valide les configurations APRES resolution des gabarits : valider avant
/// signalerait comme manquantes des valeurs qui viennent de l'heritage.
/// La validation vit ici et non dans l'UI, pour que le service et l'UI
/// partagent la meme source de verite.
/// </summary>
public sealed class ConfigurationValidator(
    INameTemplateResolver nameResolver,
    IEnumerable<IMailSourceFactory> sourceFactories,
    IEnumerable<IExportTargetFactory> exportFactories,
    IEnumerable<IMetadataWriter> metadataWriters,
    PluginManager plugins)
{
    private readonly string[] _knownProtocols = [.. sourceFactories.Select(f => f.ProtocolType)];
    private readonly string[] _knownMetadataFormats = [.. metadataWriters.Select(w => w.Format)];
    private readonly IExportTargetFactory[] _exportFactories = [.. exportFactories];

    // Les cibles du socle ET celles apportees par les plugins : une DLL d'export
    // client doit etre reconnue par la validation, sinon l'UI la signale a tort
    // comme un type inconnu. Propriete calculee et non champ : la liste change
    // apres un rechargement de plugins.
    private string[] KnownTargetTypes =>
    [
        .. _exportFactories.Select(f => f.TargetType)
            .Concat(plugins.KnownTargetTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    private IReadOnlyCollection<string> KnownProcessors => plugins.KnownProcessors;

    /// <summary>Valide un ensemble de configurations deja resolues.</summary>
    /// <param name="configurations">Configurations issues du resolveur de gabarits.</param>
    /// <returns>Les anomalies detectees, bloquantes et non bloquantes melangees.</returns>
    public IReadOnlyList<ConfigurationIssue> Validate(IReadOnlyList<PollerConfiguration> configurations) =>
        Validate(configurations, knownTemplates: null);

    /// <summary>Valide des configurations resolues, gabarits connus a l'appui.</summary>
    /// <param name="configurations">Configurations issues du resolveur.</param>
    /// <param name="knownTemplates">
    /// Gabarits declares. Quand la liste est fournie, une configuration
    /// renvoyant a un gabarit absent produit une anomalie bloquante. Le
    /// resolveur, lui, se contente de l'ecarter : il ne peut pas signaler ce
    /// qu'il vient de retirer de la liste.
    /// </param>
    /// <returns>Les anomalies detectees.</returns>
    public IReadOnlyList<ConfigurationIssue> Validate(
        IReadOnlyList<PollerConfiguration> configurations,
        IReadOnlyCollection<string>? knownTemplates)
    {
        ArgumentNullException.ThrowIfNull(configurations);

        var issues = new List<ConfigurationIssue>();

        var duplicates = configurations
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            issues.Add(new ConfigurationIssue(duplicate.Key, "Name",
                "Nom de configuration en doublon : il sert de cle de correlation et doit etre unique.", true));
        }

        foreach (var configuration in configurations)
        {
            ValidateTemplate(configuration, knownTemplates, issues);
            ValidateDataAnnotations(configuration, issues);
            ValidateSource(configuration, issues);
            ValidateOutput(configuration, issues);
            ValidateProcessors(configuration, issues);
        }

        return issues;
    }

    /// <summary>
    /// Signale un gabarit introuvable.
    /// </summary>
    /// <remarks>
    /// Bloquant : la configuration ne demarrera pas, le resolveur l'ayant
    /// ecartee. Sans cette anomalie, l'exploitant verrait une boite disparaitre
    /// de la liste des workers sans explication a l'ecran.
    /// </remarks>
    private static void ValidateTemplate(
        PollerConfiguration configuration,
        IReadOnlyCollection<string>? knownTemplates,
        List<ConfigurationIssue> issues)
    {
        if (knownTemplates is null || string.IsNullOrWhiteSpace(configuration.Template))
        {
            return;
        }

        if (!knownTemplates.Contains(configuration.Template, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ConfigurationIssue(
                configuration.Name,
                "Template",
                $"Gabarit '{configuration.Template}' introuvable. "
                + $"Gabarits declares : {string.Join(", ", knownTemplates)}.",
                true));
        }
    }

    private static void ValidateDataAnnotations(PollerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(configuration);

        if (Validator.TryValidateObject(configuration, context, results, validateAllProperties: true))
        {
            return;
        }

        foreach (var result in results)
        {
            issues.Add(new ConfigurationIssue(
                configuration.Name,
                string.Join(',', result.MemberNames),
                result.ErrorMessage ?? "Valeur invalide.",
                true));
        }
    }

    private void ValidateSource(PollerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        var source = configuration.Source;

        void Add(string path, string message, bool blocking = true) =>
            issues.Add(new ConfigurationIssue(configuration.Name, path, message, blocking));

        if (!_knownProtocols.Contains(source.Protocol, StringComparer.OrdinalIgnoreCase))
        {
            Add("Source:Protocol",
                $"Protocole '{source.Protocol}' inconnu. Disponibles : {string.Join(", ", _knownProtocols)}.");
        }

        if (string.IsNullOrWhiteSpace(source.Mailbox))
        {
            Add("Source:Mailbox", "Adresse de boite obligatoire.");
        }

        if (source.Protocol.Equals("graph", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(source.TenantId) || string.IsNullOrWhiteSpace(source.ClientId))
            {
                Add("Source:TenantId", "TenantId et ClientId obligatoires en Graph.");
            }

            if (string.IsNullOrWhiteSpace(source.ClientSecret) && string.IsNullOrWhiteSpace(source.CertificateThumbprint))
            {
                Add("Source:ClientSecret", "Un secret client ou une empreinte de certificat est obligatoire.");
            }

            if (!string.IsNullOrWhiteSpace(source.ClientSecret))
            {
                Add("Source:ClientSecret",
                    "Authentification par secret client : prevoir la date d'expiration, le certificat est preferable.",
                    blocking: false);
            }
        }
        else if (string.IsNullOrWhiteSpace(source.Host) || string.IsNullOrWhiteSpace(source.UserName))
        {
            Add("Source:Host", "Host et UserName obligatoires en IMAP.");
        }

        if (source.Disposition == MessageDisposition.Move && source.TargetFolder is null)
        {
            Add("Source:TargetFolder", "Disposition 'Move' sans dossier cible.");
        }

        if (source.Folder.WellKnown == WellKnownFolder.None)
        {
            if (string.IsNullOrWhiteSpace(source.Folder.Name))
            {
                Add("Source:Folder", "Aucun dossier designe, ni bien connu ni par nom.");
            }
            else
            {
                Add("Source:Folder:Name",
                    "Dossier designe par son libelle : fragile sur une boite localisee. Preferer WellKnown.",
                    blocking: false);
            }
        }
    }

    private void ValidateOutput(PollerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        var output = configuration.Output;

        void Add(string path, string message, bool blocking = true) =>
            issues.Add(new ConfigurationIssue(configuration.Name, path, message, blocking));

        if (!_knownMetadataFormats.Contains(output.Metadata.Format, StringComparer.OrdinalIgnoreCase))
        {
            Add("Output:Metadata:Format",
                $"Format '{output.Metadata.Format}' inconnu. Disponibles : {string.Join(", ", _knownMetadataFormats)}.");
        }

        var unknownTokens = nameResolver.Validate(output.Naming);
        if (unknownTokens.Count > 0)
        {
            Add("Output:Naming", $"Jetons inconnus : {string.Join(", ", unknownTokens)}.");
        }

        if (output.Targets.Count == 0)
        {
            Add("Output:Targets", "Aucune cible d'export : les messages seraient traites puis perdus.");
        }

        var knownTargets = KnownTargetTypes;

        foreach (var target in output.Targets)
        {
            if (!knownTargets.Contains(target.Type, StringComparer.OrdinalIgnoreCase))
            {
                Add($"Output:Targets:{target.Name}:Type",
                    $"Type de cible '{target.Type}' inconnu. Disponibles : {string.Join(", ", knownTargets)}.");
            }

            if (string.IsNullOrWhiteSpace(target.Name))
            {
                Add("Output:Targets:Name", "Toute cible doit porter un nom, sinon les erreurs sont illisibles.");
            }
        }

        if (output.PdfMode == PdfOutputMode.PerAttachment
            && !output.Naming.Contains("{index}", StringComparison.OrdinalIgnoreCase))
        {
            Add("Output:Naming",
                "Mode PerAttachment sans jeton {index} : les PDF s'ecraseraient entre eux.");
        }

        var policy = output.Policy;
        if (!policy.AtomicWrite && string.IsNullOrWhiteSpace(policy.SentinelFileName))
        {
            Add("Output:Policy:AtomicWrite",
                "Ecriture non atomique et sans fichier temoin : la GED peut lire un PDF incomplet.",
                blocking: false);
        }
    }

    private void ValidateProcessors(PollerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        var known = KnownProcessors;

        foreach (var processor in configuration.Processors)
        {
            if (!known.Contains(processor, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ConfigurationIssue(
                    configuration.Name,
                    "Processors",
                    $"Processeur '{processor}' introuvable. Plugin absent ou incompatible ?",
                    true));
            }
        }
    }
}
