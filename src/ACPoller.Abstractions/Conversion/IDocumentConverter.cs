using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;

namespace ACPoller.Abstractions.Conversion;

/// <summary>Demande de conversion d'un fichier vers PDF.</summary>
public sealed record ConversionRequest
{
    /// <summary>Fichier source deja extrait sur disque. Jamais un flux : LibreOffice travaille sur fichier.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Nom de fichier d'origine, utilise pour deduire le format.</summary>
    public required string FileName { get; init; }

    /// <summary>Type MIME declare, quand il est connu.</summary>
    public string? ContentType { get; init; }

    /// <summary>Repertoire ou deposer le PDF produit.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Chemin logique du noeud, pour la journalisation et la correlation.</summary>
    public required string LogicalPath { get; init; }

    /// <summary>Delai maximal accorde. Depasse, le process externe est tue, pas attendu.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Issue d'une conversion.</summary>
public sealed record ConversionResult
{
    /// <summary>Etat resultant du noeud.</summary>
    public required NodeStatus Status { get; init; }

    /// <summary>PDF produit. Null en cas d'echec.</summary>
    public string? PdfPath { get; init; }

    /// <summary>Nombre de pages du PDF produit, quand il est connu.</summary>
    public int? PageCount { get; init; }

    /// <summary>Echec qualifie, renseigne quand la conversion n'a pas abouti.</summary>
    public Failure? Failure { get; init; }

    /// <summary>Nom du convertisseur, conserve pour le diagnostic.</summary>
    public string? ConverterName { get; init; }

    /// <summary>Construit un resultat de conversion reussie.</summary>
    /// <param name="pdfPath">PDF produit.</param>
    /// <param name="converter">Nom du convertisseur.</param>
    /// <param name="pages">Nombre de pages, optionnel.</param>
    /// <returns>Un resultat marque comme converti.</returns>
    public static ConversionResult Success(string pdfPath, string converter, int? pages = null) =>
        new() { Status = NodeStatus.Converted, PdfPath = pdfPath, ConverterName = converter, PageCount = pages };

    /// <summary>Construit un resultat pour un fichier deja au format PDF.</summary>
    /// <param name="pdfPath">PDF repris tel quel ou repare.</param>
    /// <param name="converter">Nom du convertisseur.</param>
    /// <param name="pages">Nombre de pages, optionnel.</param>
    /// <returns>Un resultat marque comme repris tel quel.</returns>
    public static ConversionResult PassThrough(string pdfPath, string converter, int? pages = null) =>
        new() { Status = NodeStatus.PassThrough, PdfPath = pdfPath, ConverterName = converter, PageCount = pages };

    /// <summary>Construit un resultat d'echec.</summary>
    /// <param name="failure">Echec qualifie.</param>
    /// <param name="converter">Nom du convertisseur.</param>
    /// <returns>Un resultat marque comme echoue.</returns>
    public static ConversionResult Failed(Failure failure, string converter) =>
        new() { Status = NodeStatus.Failed, Failure = failure, ConverterName = converter };
}

/// <summary>
/// Convertisseur d'un format vers PDF. Les implementations sont enregistrees en
/// chaine et interrogees par priorite decroissante.
/// </summary>
public interface IDocumentConverter
{
    /// <summary>Nom du convertisseur, repris dans les journaux et le fichier d'information.</summary>
    string Name { get; }

    /// <summary>Priorite decroissante. Permet a un convertisseur client de primer sur celui du socle.</summary>
    int Priority { get; }

    /// <summary>Indique si ce convertisseur sait traiter la demande.</summary>
    /// <param name="request">Demande de conversion.</param>
    /// <returns>Vrai si le format est pris en charge.</returns>
    bool CanHandle(ConversionRequest request);

    /// <summary>Convertit le fichier en PDF.</summary>
    /// <param name="request">Demande de conversion.</param>
    /// <param name="cancellationToken">Jeton d'annulation, portant aussi le delai maximal.</param>
    /// <returns>Le resultat de la conversion, succes ou echec qualifie.</returns>
    Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Extracteur de conteneur (zip, eml, msg). Produit les enfants d'un noeud
/// sans les convertir : la conversion reste le role des <see cref="IDocumentConverter"/>.
/// </summary>
public interface IContainerExtractor
{
    /// <summary>Nom de l'extracteur, repris dans les journaux.</summary>
    string Name { get; }

    /// <summary>Indique si ce conteneur est pris en charge.</summary>
    /// <param name="fileName">Nom du fichier conteneur.</param>
    /// <param name="contentType">Type MIME declare, optionnel.</param>
    /// <returns>Vrai si l'extracteur sait ouvrir ce conteneur.</returns>
    bool CanHandle(string fileName, string? contentType);

    /// <summary>
    /// Extrait le contenu dans <paramref name="targetDirectory"/> et renseigne les
    /// enfants de <paramref name="node"/>. L'implementation respecte les garde-fous
    /// fournis : profondeur, nombre d'entrees, taille decompressee, chemins traversants.
    /// </summary>
    /// <param name="node">Noeud conteneur a developper.</param>
    /// <param name="targetDirectory">Repertoire d'extraction.</param>
    /// <param name="limits">Bornes de securite a respecter.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand l'extraction est terminee.</returns>
    Task ExtractAsync(
        DocumentNode node,
        string targetDirectory,
        ExtractionLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>Bornes de securite de l'extraction recursive. Toutes obligatoires, aucune valeur infinie.</summary>
public sealed record ExtractionLimits
{
    /// <summary>Profondeur maximale d'imbrication des conteneurs.</summary>
    public int MaxDepth { get; init; } = 5;

    /// <summary>Nombre maximal de noeuds produits pour un message.</summary>
    public int MaxNodes { get; init; } = 500;

    /// <summary>Volume decompresse maximal cumule, en octets. Protege des bombes de decompression.</summary>
    public long MaxExpandedBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Taille maximale d'une entree unique, en octets.</summary>
    public long MaxSingleEntryBytes { get; init; } = 128L * 1024 * 1024;
}
