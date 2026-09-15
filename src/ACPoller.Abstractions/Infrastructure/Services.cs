using ACPoller.Abstractions.Model;

namespace ACPoller.Abstractions.Infrastructure;

/// <summary>
/// Journal des messages deja traites, cloisonne par boite. Garantit l'idempotence
/// entre redemarrages. L'import du format historique (processed_ids.txt) est
/// explicitement au contrat : sans lui, la premiere mise en production retraite tout.
/// </summary>
public interface IProcessedStore
{
    /// <summary>Indique si un message a deja ete traite pour cette boite.</summary>
    /// <param name="mailboxId">Boite concernee.</param>
    /// <param name="deduplicationKey">Cle de deduplication du message.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Vrai si le message a deja ete exporte.</returns>
    Task<bool> IsProcessedAsync(string mailboxId, string deduplicationKey, CancellationToken cancellationToken);

    /// <summary>Enregistre un message comme traite. Appele APRES export confirme.</summary>
    /// <param name="mailboxId">Boite concernee.</param>
    /// <param name="deduplicationKey">Cle de deduplication du message.</param>
    /// <param name="processedUtc">Horodatage du traitement, en UTC.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand l'enregistrement est persiste.</returns>
    Task MarkProcessedAsync(
        string mailboxId,
        string deduplicationKey,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken);

    /// <summary>Purge au-dela de la retention configuree. Appelee par une tache de maintenance, pas a chaque cycle.</summary>
    /// <param name="mailboxId">Boite concernee.</param>
    /// <param name="threshold">Date en deca de laquelle les entrees sont supprimees.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le nombre d'entrees supprimees.</returns>
    Task<int> PurgeOlderThanAsync(string mailboxId, DateTimeOffset threshold, CancellationToken cancellationToken);

    /// <summary>Import ponctuel depuis l'ancien format de suivi.</summary>
    /// <param name="mailboxId">Boite concernee.</param>
    /// <param name="legacyFilePath">Chemin du processed_ids.txt de la v1.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le nombre d'entrees reprises.</returns>
    Task<int> ImportLegacyAsync(string mailboxId, string legacyFilePath, CancellationToken cancellationToken);
}

/// <summary>
/// Etat persiste d'un message en cours, permettant la reprise par etape.
/// Un incident ne relance pas le message entier : seuls les noeuds non traites repartent.
/// </summary>
public interface ICaptureStateStore
{
    /// <summary>Persiste l'etat courant du message.</summary>
    /// <param name="context">Contexte a enregistrer.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand l'etat est persiste.</returns>
    Task SaveAsync(CaptureContext context, CancellationToken cancellationToken);

    /// <summary>Recharge l'etat d'un message interrompu.</summary>
    /// <param name="correlationId">Identifiant de correlation.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le contexte persiste, ou null s'il n'existe pas.</returns>
    Task<CaptureContext?> LoadAsync(string correlationId, CancellationToken cancellationToken);

    /// <summary>Unites de travail restees inachevees, a reprendre au demarrage.</summary>
    /// <param name="mailboxId">Boite concernee.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les contextes inacheves, au fil de l'eau.</returns>
    IAsyncEnumerable<CaptureContext> LoadPendingAsync(string mailboxId, CancellationToken cancellationToken);

    /// <summary>Supprime l'etat d'un message mene a son terme.</summary>
    /// <param name="correlationId">Identifiant de correlation.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand l'etat est supprime.</returns>
    Task RemoveAsync(string correlationId, CancellationToken cancellationToken);
}

/// <summary>Nature d'une metrique.</summary>
public enum MetricKind
{
    /// <summary>Compteur cumulatif.</summary>
    Counter = 0,

    /// <summary>Duree, en millisecondes.</summary>
    Duration = 1,

    /// <summary>Valeur instantanee.</summary>
    Gauge = 2,
}

/// <summary>
/// Collecte de metriques. Implementations SQL Server, PostgreSQL ou Null.
/// Contrat non bloquant : aucune implementation ne doit ralentir le pipeline.
/// </summary>
public interface IMetricsProvider
{
    /// <summary>Enregistre une mesure. Ne doit jamais bloquer l'appelant.</summary>
    /// <param name="kind">Nature de la mesure.</param>
    /// <param name="name">Nom de la metrique.</param>
    /// <param name="value">Valeur mesuree.</param>
    /// <param name="tags">Etiquettes de ventilation, optionnelles.</param>
    void Record(MetricKind kind, string name, double value, IReadOnlyDictionary<string, string>? tags = null);

    /// <summary>Vide la file d'attente vers le stockage.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand les mesures sont ecrites.</returns>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>Notification sortante : acquittement, rejet metier, alerte d'exploitation.</summary>
public interface IAckSender
{
    /// <summary>Envoie la notification.</summary>
    /// <param name="message">Message a envoyer.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand l'envoi est accepte.</returns>
    Task SendAsync(AckMessage message, CancellationToken cancellationToken);
}

/// <summary>Contenu d'une notification sortante.</summary>
public sealed record AckMessage
{
    /// <summary>Destinataire.</summary>
    public required string To { get; init; }

    /// <summary>Sujet.</summary>
    public required string Subject { get; init; }

    /// <summary>Corps du message.</summary>
    public required string Body { get; init; }

    /// <summary>Vrai si le corps est en HTML.</summary>
    public bool IsHtml { get; init; }

    /// <summary>Identifiant de correlation du message d'origine, pour la tracabilite.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Chemins des fichiers a joindre.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

/// <summary>
/// Protection des secrets en configuration (mots de passe, secrets client Azure).
/// Chiffrement lie a la machine, jamais de secret en clair sur disque.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Chiffre une valeur en clair.</summary>
    /// <param name="plainText">Valeur a proteger.</param>
    /// <returns>La valeur chiffree, prefixee de son marqueur.</returns>
    string Protect(string plainText);

    /// <summary>Dechiffre une valeur protegee.</summary>
    /// <param name="protectedText">Valeur chiffree.</param>
    /// <returns>La valeur en clair.</returns>
    string Unprotect(string protectedText);

    /// <summary>Indique si une valeur est deja protegee.</summary>
    /// <param name="value">Valeur a tester.</param>
    /// <returns>Vrai si la valeur porte le marqueur de chiffrement.</returns>
    bool IsProtected(string value);
}
