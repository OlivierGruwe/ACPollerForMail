using ACPoller.Abstractions.Model;

namespace ACPoller.Abstractions.Metadata;

/// <summary>Mode de production des PDF en sortie.</summary>
public enum PdfOutputMode
{
    /// <summary>Un seul PDF par message, corps puis pieces jointes dans l'ordre de l'arbre.</summary>
    Merged = 0,

    /// <summary>Un PDF par piece jointe, plus un PDF pour le corps.</summary>
    PerAttachment = 1,

    /// <summary>Les deux, pour les GED qui indexent l'unifie et archivent le detail.</summary>
    Both = 2,
}

/// <summary>
/// Producteur du fichier d'information. Les formats json, xml et csv sont trois
/// implementations du meme contrat, alimentees par le meme arbre documentaire :
/// aucune logique metier ne doit etre dupliquee entre eux.
/// </summary>
public interface IMetadataWriter
{
    /// <summary>Format declare en configuration : "json", "xml", "csv".</summary>
    string Format { get; }

    /// <summary>Extension du fichier produit, sans point.</summary>
    string FileExtension { get; }

    /// <summary>Ecrit le fichier d'information et retourne son chemin.</summary>
    /// <param name="context">Contexte complet du message.</param>
    /// <param name="outputDirectory">Repertoire de sortie.</param>
    /// <param name="fileNameWithoutExtension">Nom de base, deja resolu par le moteur de jetons.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le chemin du fichier produit.</returns>
    Task<string> WriteAsync(
        CaptureContext context,
        string outputDirectory,
        string fileNameWithoutExtension,
        CancellationToken cancellationToken);
}

/// <summary>Assembleur de PDF. Isole pour pouvoir changer de moteur sans toucher au pipeline.</summary>
public interface IPdfAssembler
{
    /// <summary>
    /// Fusionne les PDF des noeuds dans l'ordre, avec un signet par noeud.
    /// Ecriture atomique : fichier temporaire puis deplacement, jamais d'ecriture en place.
    /// </summary>
    /// <param name="parts">Noeuds a fusionner, dans l'ordre voulu.</param>
    /// <param name="outputPath">Chemin du PDF unifie a produire.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le chemin du PDF produit.</returns>
    Task<string> MergeAsync(IReadOnlyList<DocumentNode> parts, string outputPath, CancellationToken cancellationToken);

    /// <summary>Tente de reparer un PDF corrompu.</summary>
    /// <param name="pdfPath">PDF a reparer.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le chemin du PDF repare, ou null s'il est irrecuperable.</returns>
    Task<string?> RepairAsync(string pdfPath, CancellationToken cancellationToken);
}

/// <summary>
/// Resolution des jetons de nommage ({name}, {date}, {time}, {guid}, {subject},
/// {mailbox}, {index}...). Moteur unique, partage par les exports et les writers,
/// pour eviter les divergences de nomenclature entre cibles.
/// </summary>
public interface INameTemplateResolver
{
    /// <summary>Resout un gabarit en nom de fichier.</summary>
    /// <param name="nameTemplate">Gabarit a jetons.</param>
    /// <param name="context">Contexte du message.</param>
    /// <param name="node">Noeud concerne, pour les sorties par piece jointe.</param>
    /// <param name="index">Indice de la piece, pour le jeton {index}.</param>
    /// <returns>Le nom resolu, sans extension.</returns>
    string Resolve(string nameTemplate, CaptureContext context, DocumentNode? node = null, int? index = null);

    /// <summary>Valide un gabarit, pour l'UI de parametrage.</summary>
    /// <param name="nameTemplate">Gabarit a verifier.</param>
    /// <returns>La liste des jetons inconnus, vide si le gabarit est valide.</returns>
    IReadOnlyList<string> Validate(string nameTemplate);
}
