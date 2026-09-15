using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Configuration;

namespace ACPoller.Abstractions.Export;

/// <summary>Nature d'un fichier depose sur une cible d'export.</summary>
public enum ExportItemKind
{
    /// <summary>PDF unifie du message.</summary>
    MergedPdf = 0,

    /// <summary>PDF d'une piece jointe.</summary>
    AttachmentPdf = 1,

    /// <summary>Fichier d'information (json, xml, csv).</summary>
    Metadata = 2,

    /// <summary>Piece jointe d'origine, si la configuration demande de la conserver.</summary>
    OriginalAttachment = 3,

    /// <summary>MIME brut archive.</summary>
    RawMessage = 4,
}

/// <summary>Fichier unitaire a deposer sur une cible.</summary>
public sealed record ExportItem
{
    /// <summary>Chemin du fichier dans le repertoire de travail.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Nom relatif attendu sur la cible, deja resolu par le moteur de jetons.</summary>
    public required string RelativeName { get; init; }

    /// <summary>Nature du fichier, pour les cibles qui routent selon le type.</summary>
    public required ExportItemKind Kind { get; init; }

    /// <summary>Chemin logique du noeud d'origine, null pour un fichier de niveau message.</summary>
    public string? LogicalPath { get; init; }
}

/// <summary>Politique d'ecriture exigee par la GED cible.</summary>
public sealed record ExportPolicy
{
    /// <summary>
    /// Ecriture en fichier temporaire puis renommage. Evite qu'une GED lise un
    /// PDF incomplet en cours d'ecriture.
    /// </summary>
    public bool AtomicWrite { get; init; } = true;

    /// <summary>
    /// Nom du fichier temoin ecrit en dernier, une fois tous les fichiers
    /// deposes. Null pour desactiver.
    /// </summary>
    public string? SentinelFileName { get; init; }

    /// <summary>Tout le lot doit reussir, sinon rollback de ce qui a ete depose.</summary>
    public bool AllOrNothing { get; init; } = true;

    /// <summary>Nombre maximal de tentatives sur echec transitoire.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Delai avant la seconde tentative. Double a chaque nouvel essai.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Lot indissociable de fichiers issus d'un meme message.</summary>
public sealed record ExportBatch
{
    /// <summary>Identifiant de correlation du message, repris dans les journaux.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Contexte complet du message, pour les cibles qui exploitent les metadonnees.</summary>
    public required CaptureContext Context { get; init; }

    /// <summary>Fichiers a deposer, dans l'ordre.</summary>
    public required IReadOnlyList<ExportItem> Items { get; init; }

    /// <summary>Politique d'ecriture applicable a ce lot.</summary>
    public required ExportPolicy Policy { get; init; }
}

/// <summary>Issue d'un depot sur une cible.</summary>
public sealed record ExportResult
{
    /// <summary>Vrai si tout le lot a ete depose.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Detail de l'echec, qualifie transitoire ou permanent par la cible
    /// elle-meme. Null en cas de succes.
    /// </summary>
    public Failure? Failure { get; init; }

    /// <summary>
    /// References des objets ecrits (chemin UNC, URI S3, chemin FTP), pour
    /// l'acquittement, l'audit et un eventuel rollback.
    /// </summary>
    public IReadOnlyList<string> WrittenReferences { get; init; } = [];

    /// <summary>Construit un resultat de succes.</summary>
    /// <param name="references">References des objets ecrits.</param>
    /// <returns>Un resultat marque comme reussi.</returns>
    public static ExportResult Ok(IReadOnlyList<string> references) =>
        new() { Success = true, WrittenReferences = references };

    /// <summary>Construit un resultat d'echec.</summary>
    /// <param name="failure">Echec qualifie, transitoire ou permanent.</param>
    /// <returns>Un resultat marque comme echoue.</returns>
    public static ExportResult Ko(Failure failure) =>
        new() { Success = false, Failure = failure };
}

/// <summary>
/// Cible d'export. Une implementation par transport : systeme de fichiers,
/// FTP(s), S3, ou assemblage client. Une configuration peut en declarer
/// plusieurs, auquel cas la politique du lot decide du comportement en cas
/// d'echec partiel.
/// </summary>
public interface IExportTarget : IConnectionTestable, IAsyncDisposable
{
    /// <summary>Nom d'instance issu de la configuration, pour la journalisation.</summary>
    string Name { get; }

    /// <summary>
    /// Depose le lot. L'implementation respecte la politique portee par
    /// <paramref name="batch"/> et qualifie tout echec via
    /// <see cref="Failure"/> : c'est elle qui sait si un rejeu a du sens.
    /// </summary>
    /// <param name="batch">Lot indissociable a deposer.</param>
    /// <param name="cancellationToken">Jeton d'annulation, propage jusqu'aux appels reseau.</param>
    /// <returns>Le resultat du depot, succes ou echec qualifie.</returns>
    Task<ExportResult> ExportAsync(ExportBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Annule un depot partiel. Appele quand une autre cible du meme lot a
    /// echoue en mode tout ou rien. Doit etre idempotent : une reference deja
    /// absente n'est pas une erreur.
    /// </summary>
    /// <param name="batch">Lot concerne.</param>
    /// <param name="writtenReferences">References effectivement ecrites, a retirer.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand le nettoyage est termine.</returns>
    Task RollbackAsync(ExportBatch batch, IReadOnlyList<string> writtenReferences, CancellationToken cancellationToken);
}

/// <summary>
/// Fabrique de cible, resolue par le type declare en configuration
/// ("fs", "ftp", "s3", "plugin"). C'est le point d'extension officiel pour une
/// DLL d'export client.
/// </summary>
public interface IExportTargetFactory
{
    /// <summary>Type de transport gere, tel qu'il apparait en configuration.</summary>
    string TargetType { get; }

    /// <summary>Instancie une cible a partir de sa section de configuration.</summary>
    /// <param name="services">Fournisseur de services du socle (journalisation, secrets).</param>
    /// <param name="configuration">Section de configuration propre a cette cible.</param>
    /// <returns>La cible prete a l'emploi.</returns>
    IExportTarget Create(IServiceProvider services, IConfiguration configuration);
}
