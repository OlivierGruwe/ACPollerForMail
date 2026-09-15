using ACPoller.Abstractions.Common;

namespace ACPoller.Abstractions.Model;

/// <summary>
/// Identite d'un message. On distingue volontairement l'identifiant technique du
/// backend (instable : il change quand le message est deplace de dossier sous Graph)
/// de l'identifiant de deduplication, seul a etre persiste dans le ProcessedStore.
/// </summary>
public sealed record MessageIdentity
{
    /// <summary>Identifiant natif du backend (uid IMAP, id Graph). Instable.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Message-Id RFC 5322. Cle de deduplication a privilegier.</summary>
    public string? InternetMessageId { get; init; }

    /// <summary>Cle effective de deduplication : InternetMessageId sinon empreinte du MIME.</summary>
    public required string DeduplicationKey { get; init; }
}

/// <summary>En-tetes et metadonnees du message, independants du backend.</summary>
public sealed record MessageEnvelope
{
    /// <summary>Sujet du message, tel que recu.</summary>
    public string? Subject { get; init; }

    /// <summary>Expediteur.</summary>
    public string? From { get; init; }

    /// <summary>Destinataires principaux.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Destinataires en copie.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Date de reception dans la boite, en UTC.</summary>
    public DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>Date d'envoi declaree, en UTC. Absente sur certains messages.</summary>
    public DateTimeOffset? SentUtc { get; init; }

    /// <summary>En-tetes bruts, exploitables par les traitements metier.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Unite de travail complete d'un message, du telechargement a l'export.
/// Elle est serialisable : c'est le manifest persiste dans le repertoire de travail,
/// qui permet la reprise apres incident sans retelecharger ni reconvertir.
/// </summary>
public sealed class CaptureContext
{
    /// <summary>Identifiant de correlation, stable entre tentatives. Cle du state store.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Boite d'origine. Cloisonne le journal des messages traites.</summary>
    public required string MailboxId { get; init; }

    /// <summary>Nom de la configuration ayant produit ce contexte.</summary>
    public required string ConfigurationName { get; init; }

    /// <summary>Identite du message.</summary>
    public required MessageIdentity Identity { get; init; }

    /// <summary>En-tetes et metadonnees du message.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>Racine de l'arbre documentaire. Le corps et les PJ sont ses enfants.</summary>
    public required DocumentNode Root { get; init; }

    /// <summary>Repertoire de travail dedie au message. Nettoye apres export reussi.</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Chemin du MIME brut telecharge, conserve pour rejeu.</summary>
    public string? RawMessagePath { get; set; }

    /// <summary>Numero de tentative, incremente a chaque reprise.</summary>
    public int Attempt { get; set; }

    /// <summary>Debut du traitement, en UTC.</summary>
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Sac de proprietes partage entre le core et les plugins metier
    /// (code societe, fournisseur extrait du sujet, code de rejet...).
    /// C'est le canal officiel : un plugin n'ecrit jamais dans le fichier d'info directement.
    /// </summary>
    public IDictionary<string, string> Properties { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Echecs non bloquants accumules, restitues dans le fichier d'information.</summary>
    public IList<Failure> Warnings { get; } = [];

    /// <summary>
    /// Champs personnalises resolus pour le fichier d'information. Distincts
    /// de Properties : ceux-ci sont DESTINES A LA SORTIE et leur ordre est
    /// celui de la configuration, alors que Properties est un canal de travail
    /// entre le pipeline et les traitements metier.
    /// </summary>
    public IDictionary<string, string> CustomFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
