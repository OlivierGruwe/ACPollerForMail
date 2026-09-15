using ACPoller.Abstractions.Model;

namespace ACPoller.Core.Metadata;

/// <summary>
/// Projection du contexte destinee aux fichiers d'information.
/// </summary>
/// <remarks>
/// Une projection dediee, et non le modele de domaine serialise directement.
/// Deux raisons : ce fichier est un CONTRAT avec la GED cliente, il ne doit pas
/// changer parce qu'on a ajoute une propriete interne au pipeline ; et il ne
/// doit exposer ni chemins de travail ni details d'implementation.
///
/// Les trois formats, json, xml et csv, partent de cette meme projection : une
/// GED qui bascule de l'un a l'autre retrouve exactement les memes valeurs.
/// </remarks>
public sealed record CaptureManifest
{
    /// <summary>Version du format, incrementee a chaque rupture du contrat GED.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Identifiant de correlation, reportable dans les journaux du service.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Nom de la configuration ayant produit le lot.</summary>
    public required string Configuration { get; init; }

    /// <summary>Boite d'origine.</summary>
    public required string Mailbox { get; init; }

    /// <summary>Message-Id du message, ou empreinte a defaut.</summary>
    public required string MessageId { get; init; }

    /// <summary>Sujet.</summary>
    public string? Subject { get; init; }

    /// <summary>Expediteur.</summary>
    public string? From { get; init; }

    /// <summary>Destinataires principaux.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Destinataires en copie.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Date de reception, en UTC.</summary>
    public DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>Date de traitement, en UTC.</summary>
    public DateTimeOffset ProcessedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Nombre de tentatives. Superieur a 1 signale une reprise apres incident.</summary>
    public int Attempt { get; init; }

    /// <summary>Donnees ajoutees par les traitements metier (fournisseur, code societe...).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Pieces du message, dans l'ordre de l'arbre.</summary>
    public IReadOnlyList<ManifestItem> Items { get; init; } = [];

    /// <summary>Anomalies non bloquantes rencontrees pendant le traitement.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Nombre de pieces non converties, y compris substituees.</summary>
    public int FailedCount => Items.Count(i => !i.Converted);

    /// <summary>Champs personnalises declares en configuration.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Construit la projection depuis le contexte de traitement.</summary>
    /// <param name="context">Contexte du message.</param>
    /// <returns>La projection prete a serialiser.</returns>
    public static CaptureManifest Create(CaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new CaptureManifest
        {
            CorrelationId = context.CorrelationId,
            Configuration = context.ConfigurationName,
            Mailbox = context.MailboxId,
            MessageId = context.Identity.InternetMessageId ?? context.Identity.DeduplicationKey,
            Subject = context.Envelope.Subject,
            From = context.Envelope.From,
            To = context.Envelope.To,
            Cc = context.Envelope.Cc,
            ReceivedUtc = context.Envelope.ReceivedUtc,
            Attempt = context.Attempt,
            Properties = new Dictionary<string, string>(context.Properties),
            Items = [.. BuildItems(context)],
            Warnings = [.. context.Warnings.Select(w => w.Reason)],
            Fields = new Dictionary<string, string>(context.CustomFields, StringComparer.Ordinal),
        };
    }

    private static IEnumerable<ManifestItem> BuildItems(CaptureContext context)
    {
        // Les conteneurs sont exclus : ils ne produisent aucun document, seuls
        // leurs enfants comptent pour la GED.
        foreach (var node in context.Root.Walk().Where(n => n.Status != NodeStatus.Container))
        {
            yield return new ManifestItem
            {
                Path = node.LogicalPath,
                FileName = node.FileName,
                Kind = node.Kind.ToString(),
                ContentType = node.ContentType,
                SizeBytes = node.SizeBytes,
                Sha256 = node.Sha256,
                Status = node.Status.ToString(),
                Converted = node.Status is NodeStatus.Converted or NodeStatus.PassThrough,
                Substituted = node.Status == NodeStatus.Substituted,
                Unverified = node.Status == NodeStatus.PassThroughUnverified,
                PageCount = node.PageCount,

                // Nom du fichier PDF seul, jamais le chemin de travail : la GED
                // n'a rien a savoir de l'arborescence interne du service.
                PdfFileName = node.PdfPath is null ? null : Path.GetFileName(node.PdfPath),
                FailureReason = node.Failure?.Reason,
            };
        }
    }
}

/// <summary>Une piece du message, telle qu'exposee a la GED.</summary>
public sealed record ManifestItem
{
    /// <summary>Chemin logique dans le message, ex. "transfert.eml/bl.pdf".</summary>
    public required string Path { get; init; }

    /// <summary>Nom de fichier d'origine.</summary>
    public required string FileName { get; init; }

    /// <summary>Nature de la piece : Body, Attachment, Archive, EmbeddedMessage, InlineImage.</summary>
    public required string Kind { get; init; }

    /// <summary>Type MIME declare.</summary>
    public string? ContentType { get; init; }

    /// <summary>Taille de la piece d'origine, en octets.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Empreinte SHA-256 de la piece d'origine.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Etat de traitement detaille.</summary>
    public required string Status { get; init; }

    /// <summary>Vrai si un PDF fidele a ete produit.</summary>
    public bool Converted { get; init; }

    /// <summary>
    /// Vrai si le PDF present est une page de substitution et non la piece.
    /// Distinguer les deux est essentiel : un document substitue est present
    /// dans le lot mais son contenu reste manquant.
    /// </summary>
    public bool Substituted { get; init; }

    /// <summary>Nombre de pages du PDF produit.</summary>
    public int? PageCount { get; init; }

    /// <summary>Nom du PDF produit, sans chemin.</summary>
    public string? PdfFileName { get; init; }

    /// <summary>Motif d'echec, renseigne quand la piece n'a pas ete convertie.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Vrai si le document est livre mais absent du PDF unifie, faute d'avoir
    /// pu etre lu. La GED a le fichier, l'unifie est incomplet.
    /// </summary>
    public bool Unverified { get; init; }
}
