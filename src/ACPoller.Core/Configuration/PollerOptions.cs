using System.ComponentModel.DataAnnotations;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Sources;

namespace ACPoller.Core.Configuration;

/// <summary>
/// Racine de la configuration. Les proprietes sont mutables et publiques :
/// c'est une exigence du binder de configuration, pas un choix de style.
/// </summary>
public sealed class PollerOptions
{
    public const string SectionName = "Poller";

    /// <summary>
    /// Version du schema de configuration. Permet a la migration de savoir
    /// quoi transformer sans deviner. Toute rupture l'incremente.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    [Required]
    public string WorkDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Concurrence GLOBALE, tous workers confondus. En v1 la borne etait par
    /// boite, ce qui laissait 103 boites saturer Graph et le disque ensemble.
    /// </summary>
    [Range(1, 128)]
    public int MaxGlobalConcurrency { get; set; } = 8;

    public string? PluginDirectory { get; set; }

    /// <summary>Retention du journal des messages traites, en jours.</summary>
    [Range(1, 3650)]
    public int ProcessedRetentionDays { get; set; } = 90;

    /// <summary>
    /// Gabarits reutilisables, indexes par nom. Une configuration qui reference
    /// un gabarit herite de toutes ses valeurs et ne surcharge que ses ecarts.
    /// </summary>
    public Dictionary<string, PollerConfiguration> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PollerConfiguration> Configurations { get; set; } = [];
}

/// <summary>
/// Configuration d'une boite. Sert aussi de gabarit : les deux ont exactement
/// la meme forme, ce qui evite un second modele a maintenir en parallele.
/// </summary>
public sealed class PollerConfiguration
{
    /// <summary>Identifiant unique. Sert de cle de correlation dans les logs et metriques.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Nom du gabarit dont heriter. Un seul niveau, pas de chainage.</summary>
    public string? Template { get; set; }

    public SourceOptions Source { get; set; } = new();

    public QueryOptions Query { get; set; } = new();

    public ScheduleOptions Schedule { get; set; } = new();

    public ExtractionOptions Extraction { get; set; } = new();

    public ConversionOptions Conversion { get; set; } = new();

    public OutputOptions Output { get; set; } = new();

    /// <summary>Noms des <c>ICaptureProcessor</c> a activer, dans l'ordre declare.</summary>
    public List<string> Processors { get; set; } = [];
}

public sealed class SourceOptions
{
    /// <summary>"imap" ou "graph". Resolu par <c>IMailSourceFactory.ProtocolType</c>.</summary>
    public string Protocol { get; set; } = "graph";

    /// <summary>Adresse de la boite. C'est la seule valeur qui differe entre boites d'un meme tenant.</summary>
    public string Mailbox { get; set; } = string.Empty;

    // --- Graph ---
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }

    /// <summary>Secret client, chiffre au repos (prefixe ENC:). Jamais en clair sur disque.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Empreinte du certificat, alternative recommandee au secret client.</summary>
    public string? CertificateThumbprint { get; set; }

    // --- IMAP ---
    public string? Host { get; set; }
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }

    // --- Commun ---
    public FolderOptions Folder { get; set; } = new() { WellKnown = WellKnownFolder.Inbox };

    public MessageDisposition Disposition { get; set; } = MessageDisposition.MarkAsRead;

    public FolderOptions? TargetFolder { get; set; }

    public ThrottlingOptions Throttling { get; set; } = new();
}

/// <summary>
/// Designation d'un dossier. <see cref="WellKnown"/> prime sur <see cref="Name"/> :
/// le libelle localise ("Boite de reception") ne doit jamais servir de cle.
/// </summary>
public sealed class FolderOptions
{
    public WellKnownFolder WellKnown { get; set; } = WellKnownFolder.None;

    public string? Name { get; set; }
}

public sealed class ThrottlingOptions
{
    [Range(1, 64)]
    public int MaxConcurrent { get; set; } = 8;

    [Range(0, 10000)]
    public int MinSpacingMs { get; set; } = 50;

    /// <summary>Respect du Retry-After renvoye sur 429. A ne desactiver qu'en diagnostic.</summary>
    public bool HonorRetryAfter { get; set; } = true;
}

public sealed class QueryOptions
{
    public bool UnreadOnly { get; set; } = true;

    [Range(1, 5000)]
    public int MaxCount { get; set; } = 200;

    public bool RequireAttachments { get; set; }

    /// <summary>Age maximal des messages repris au premier demarrage, en jours.</summary>
    [Range(0, 3650)]
    public int InitialLookbackDays { get; set; } = 7;

    /// <summary>
    /// Date plancher absolue : aucun message anterieur n'est jamais traite.
    /// A renseigner a la date de mise en production quand l'historique n'est
    /// pas repris. Contrairement a InitialLookbackDays, elle ne glisse pas
    /// au redemarrage : c'est ce qui garantit qu'un redemarrage six mois plus
    /// tard ne rattrape pas l'historique.
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }
}

public sealed class ScheduleOptions
{
    [Range(5, 86400)]
    public int IntervalSeconds { get; set; } = 120;

    /// <summary>Decalage aleatoire au demarrage, pour ne pas lancer 100 workers a la meme seconde.</summary>
    [Range(0, 600)]
    public int StartupJitterSeconds { get; set; } = 30;
}

public sealed class ExtractionOptions
{
    [Range(1, 20)]
    public int MaxDepth { get; set; } = 5;

    [Range(1, 10000)]
    public int MaxNodes { get; set; } = 500;

    [Range(1024, 10L * 1024 * 1024 * 1024)]
    public long MaxExpandedBytes { get; set; } = 512L * 1024 * 1024;

    [Range(1024, 10L * 1024 * 1024 * 1024)]
    public long MaxSingleEntryBytes { get; set; } = 128L * 1024 * 1024;

    public ExtractionLimits ToLimits() => new()
    {
        MaxDepth = MaxDepth,
        MaxNodes = MaxNodes,
        MaxExpandedBytes = MaxExpandedBytes,
        MaxSingleEntryBytes = MaxSingleEntryBytes,
    };
}

/// <summary>Que faire d'une piece jointe que l'on ne sait pas convertir.</summary>
public enum ConversionFailurePolicy
{
    /// <summary>Produire un PDF de substitution portant le motif, et poursuivre.</summary>
    Substitute = 0,

    /// <summary>Poursuivre sans PDF pour ce noeud, statut degrade dans le fichier d'information.</summary>
    Skip = 1,

    /// <summary>Rejeter le message entier. Rien n'est exporte.</summary>
    RejectMessage = 2,
}

public sealed class ConversionOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public ConversionFailurePolicy OnFailure { get; set; } = ConversionFailurePolicy.Substitute;

    /// <summary>Joindre les pieces d'origine a la sortie, en plus des PDF.</summary>
    public bool KeepOriginals { get; set; }

    /// <summary>Convertir le corps du message en PDF. Desactive, seules les PJ sortent.</summary>
    public bool IncludeBody { get; set; } = true;

    /// <summary>Extensions ecartees avant conversion, ex. .p7s, .ics, signatures.</summary>
    public List<string> ExcludedExtensions { get; set; } = [];

    /// <summary>Taille minimale d'une image inline pour etre conservee, en octets. Ecarte les logos de signature.</summary>
    public long MinInlineImageBytes { get; set; } = 20 * 1024;
}

public sealed class OutputOptions
{
    public PdfOutputMode PdfMode { get; set; } = PdfOutputMode.Merged;

    public MetadataOptions Metadata { get; set; } = new();

    /// <summary>Gabarit de nommage a jetons. Valide par <c>INameTemplateResolver</c>.</summary>
    public string Naming { get; set; } = "{date}{time}_{mailbox}_{guid}";

    public List<TargetOptions> Targets { get; set; } = [];

    public PolicyOptions Policy { get; set; } = new();
}

public sealed class MetadataOptions
{
    /// <summary>"json", "xml" ou "csv". Resolu par <c>IMetadataWriter.Format</c>.</summary>
    public string Format { get; set; } = "json";

    /// <summary>Gabarit de mise en forme propre au client, optionnel.</summary>
    public string? Template { get; set; }

    /// <summary>
    /// Champs ajoutes au fichier d'information, dans l'ordre de declaration.
    /// L'ordre fait foi : c'est celui des colonnes du CSV.
    /// </summary>
    public List<MetadataFieldOptions> Fields { get; set; } = [];
    /// <summary>
    /// Extension du fichier produit, sans point. Utile en format xslt, ou la
    /// sortie peut etre du texte plat ou un .idx attendu par la GED.
    /// </summary>
    public string? Extension { get; set; }
}

public sealed class TargetOptions
{
    /// <summary>"fs", "ftp", "s3" ou "plugin". Resolu par <c>IExportTargetFactory.TargetType</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Nom d'instance, utilise dans les logs et les messages d'erreur.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Reglages propres au transport, laisses libres : c'est la fabrique qui les
    /// interprete. Un nouveau transport n'oblige pas a modifier ce modele.
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Politique propre a cette cible. Null : celle de la sortie s'applique.</summary>
    public PolicyOptions? Policy { get; set; }
}

public sealed class PolicyOptions
{
    public bool AtomicWrite { get; set; } = true;

    public string? SentinelFileName { get; set; }

    public bool AllOrNothing { get; set; } = true;

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 3;

    [Range(1, 600)]
    public int InitialBackoffSeconds { get; set; } = 2;
}
