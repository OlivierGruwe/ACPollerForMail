using ACPoller.Abstractions.Common;

namespace ACPoller.Abstractions.Model;

/// <summary>Nature d'un noeud de l'arbre documentaire.</summary>
public enum DocumentKind
{
    /// <summary>Corps du message (texte ou HTML). Peut etre absent.</summary>
    Body = 0,

    /// <summary>Piece jointe simple.</summary>
    Attachment = 1,

    /// <summary>Archive (zip, 7z...) dont le contenu est expose dans les enfants.</summary>
    Archive = 2,

    /// <summary>Message imbrique (eml, msg) dont le contenu est expose dans les enfants.</summary>
    EmbeddedMessage = 3,

    /// <summary>Image incorporee referencee par le corps HTML (cid:).</summary>
    InlineImage = 4,
}

/// <summary>Etat de traitement d'un noeud.</summary>
public enum NodeStatus
{
    /// <summary>Extrait, pas encore converti.</summary>
    Pending = 0,

    /// <summary>Converti avec succes, <see cref="DocumentNode.PdfPath"/> renseigne.</summary>
    Converted = 1,

    /// <summary>Deja au format PDF, repris tel quel ou apres reparation.</summary>
    PassThrough = 2,

    /// <summary>Ecarte volontairement (filtre metier, politique de configuration).</summary>
    Excluded = 3,

    /// <summary>Echec de conversion. <see cref="DocumentNode.Failure"/> renseigne.</summary>
    Failed = 4,

    /// <summary>Conteneur : ne produit pas de PDF, seuls ses enfants comptent.</summary>
    Container = 5,

    /// <summary>
    /// Non convertie, mais un PDF de substitution a ete produit a sa place.
    /// Statut distinct de Converted : le document part en sortie, mais la piece
    /// d'origine reste manquante et le motif est conserve dans Failure.
    /// </summary>
    Substituted = 6,
    /// <summary>
    /// PDF livre tel quel sans avoir pu etre valide. Le document part en
    /// sortie, mais il est ABSENT du PDF unifie : nous ne savons pas le lire
    /// pour le fusionner. Distinguer ce cas de PassThrough est essentiel, sinon
    /// personne ne sait que le PDF unifie est incomplet.
    /// </summary>
    PassThroughUnverified = 7,
}

/// <summary>
/// Noeud de l'arbre documentaire d'un message. Un message n'est pas une liste plate
/// de fichiers : une archive et un eml imbrique portent des enfants, et le chemin
/// logique conserve cette hierarchie jusqu'au fichier d'information.
/// </summary>
public sealed class DocumentNode
{
    private readonly List<DocumentNode> _children = [];

    /// <summary>Construit un noeud.</summary>
    /// <param name="logicalPath">Chemin logique unique dans le message.</param>
    /// <param name="kind">Nature du noeud.</param>
    /// <param name="fileName">Nom de fichier d'origine.</param>
    public DocumentNode(string logicalPath, DocumentKind kind, string fileName)
    {
        LogicalPath = logicalPath;
        Kind = kind;
        FileName = fileName;
    }

    /// <summary>
    /// Chemin logique unique et stable dans le message, ex. "transfert.eml/bl.pdf".
    /// Sert d'identifiant de reprise : c'est la cle qui permet de ne rejouer que
    /// les noeuds non traites apres un incident.
    /// </summary>
    public string LogicalPath { get; }

    /// <summary>Nature du noeud.</summary>
    public DocumentKind Kind { get; }

    /// <summary>Nom de fichier d'origine, non assaini. L'assainissement est du ressort de l'export.</summary>
    public string FileName { get; }

    /// <summary>Ordre deterministe entre freres. Garantit une fusion PDF reproductible.</summary>
    public int Order { get; set; }

    /// <summary>Type MIME declare, quand il est connu.</summary>
    public string? ContentType { get; set; }

    /// <summary>Taille du contenu source, en octets.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Empreinte du contenu source, pour la deduplication et la tracabilite.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Fichier extrait dans le repertoire de travail. Null pour un conteneur pur.</summary>
    public string? SourcePath { get; set; }

    /// <summary>PDF produit pour ce noeud. Null tant que la conversion n'a pas abouti.</summary>
    public string? PdfPath { get; set; }

    /// <summary>Nombre de pages du PDF produit, quand le convertisseur le fournit.</summary>
    public int? PageCount { get; set; }

    /// <summary>Etat de traitement courant.</summary>
    public NodeStatus Status { get; set; } = NodeStatus.Pending;

    /// <summary>Echec qualifie, renseigne quand <see cref="Status"/> vaut Failed ou Excluded.</summary>
    public Failure? Failure { get; set; }

    /// <summary>Nom du convertisseur ayant produit le PDF, pour le diagnostic.</summary>
    public string? ConvertedBy { get; set; }

    /// <summary>Profondeur d'imbrication, controlee par les garde-fous de recursion.</summary>
    public int Depth { get; set; }

    /// <summary>Proprietes libres alimentees par les plugins metier.</summary>
    public IDictionary<string, string> Properties { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enfants du noeud, dans l'ordre d'ajout.</summary>
    public IReadOnlyList<DocumentNode> Children => _children;

    /// <summary>Ajoute un enfant en fixant sa profondeur et son ordre.</summary>
    /// <param name="child">Noeud a rattacher.</param>
    /// <returns>Le noeud ajoute, pour permettre le chainage.</returns>
    public DocumentNode AddChild(DocumentNode child)
    {
        child.Depth = Depth + 1;
        child.Order = _children.Count;
        _children.Add(child);
        return child;
    }

    /// <summary>Parcours prefixe de l'arbre, racine comprise.</summary>
    /// <returns>Tous les noeuds du sous-arbre, dans l'ordre de parcours.</returns>
    public IEnumerable<DocumentNode> Walk()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var node in child.Walk())
            {
                yield return node;
            }
        }
    }

    /// <summary>Noeuds produisant un PDF, dans l'ordre de fusion.</summary>
    /// <returns>Les noeuds convertis ou repris tels quels.</returns>
    public IEnumerable<DocumentNode> PdfParts() =>
        Walk().Where(n => n.PdfPath is not null && n.Status is NodeStatus.Converted or NodeStatus.PassThrough);
}
