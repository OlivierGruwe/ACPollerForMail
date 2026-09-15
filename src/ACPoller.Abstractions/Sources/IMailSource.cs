using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Configuration;

namespace ACPoller.Abstractions.Sources;

/// <summary>
/// Reference vers un dossier, resolue par la source. Les dossiers bien connus sont
/// designes par leur nom canonique et non par leur libelle localise : c'est ce qui
/// cassait "inbox=Inbox" sur les boites francaises.
/// </summary>
/// <param name="Name">Nom du dossier, utilise quand aucun dossier bien connu n'est designe.</param>
/// <param name="WellKnown">Dossier canonique, prioritaire sur le nom.</param>
public sealed record FolderRef(string Name, WellKnownFolder WellKnown = WellKnownFolder.None)
{
    /// <summary>Boite de reception, quelle que soit la langue de la boite.</summary>
    public static FolderRef Inbox => new("Inbox", WellKnownFolder.Inbox);

    /// <summary>Dossier d'archive, quelle que soit la langue de la boite.</summary>
    public static FolderRef Archive => new("Archive", WellKnownFolder.Archive);
}

/// <summary>Dossiers canoniques, independants de la langue de la boite.</summary>
public enum WellKnownFolder
{
    /// <summary>Aucun : le dossier est designe par son nom.</summary>
    None = 0,

    /// <summary>Boite de reception.</summary>
    Inbox = 1,

    /// <summary>Archive.</summary>
    Archive = 2,

    /// <summary>Elements envoyes.</summary>
    SentItems = 3,

    /// <summary>Elements supprimes.</summary>
    DeletedItems = 4,
}

/// <summary>Message repere lors du listage, avant tout telechargement.</summary>
public sealed record MessageRef
{
    /// <summary>Identite du message.</summary>
    public required MessageIdentity Identity { get; init; }

    /// <summary>En-tetes et metadonnees, obtenus sans telecharger le corps.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>Indicateur de presence de pieces jointes, quand le backend le fournit.</summary>
    public bool HasAttachments { get; init; }

    /// <summary>Taille estimee du message, en octets, quand elle est connue.</summary>
    public long? EstimatedSize { get; init; }
}

/// <summary>Action a appliquer a un message une fois traite.</summary>
public enum MessageDisposition
{
    /// <summary>Ne rien faire : le message reste tel quel dans la boite.</summary>
    None = 0,

    /// <summary>Marquer comme lu.</summary>
    MarkAsRead = 1,

    /// <summary>Deplacer vers le dossier cible declare en configuration.</summary>
    Move = 2,

    /// <summary>Supprimer.</summary>
    Delete = 3,
}

/// <summary>Criteres de listage, appliques cote serveur quand le backend le permet.</summary>
public sealed record MessageQuery
{
    /// <summary>Ne prendre que les messages non lus.</summary>
    public bool UnreadOnly { get; init; } = true;

    /// <summary>Ne prendre que les messages recus apres cette date.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Nombre maximal de messages ramenes par cycle.</summary>
    public int? MaxCount { get; init; }

    /// <summary>Ne prendre que les messages porteurs de pieces jointes.</summary>
    public bool RequireAttachments { get; init; }
}

/// <summary>
/// Source de messages. IMAP et Graph exposent tous deux le MIME brut
/// (FETCH BODY[] / GET /messages/{id}/$value), ce qui permet un seul pipeline
/// de parsing en aval au lieu de deux modeles objets divergents.
/// </summary>
public interface IMailSource : IConnectionTestable, IAsyncDisposable
{
    /// <summary>Identifiant de la boite, utilise comme cle du ProcessedStore.</summary>
    string MailboxId { get; }

    /// <summary>Resout un dossier, en tolerant les libelles localises.</summary>
    /// <param name="folder">Dossier demande, canonique ou nomme.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le dossier resolu tel que le backend le designe.</returns>
    Task<FolderRef> ResolveFolderAsync(FolderRef folder, CancellationToken cancellationToken);

    /// <summary>
    /// Liste les messages a traiter. Le streaming evite de materialiser des milliers
    /// d'entrees et rend la boucle interruptible par le jeton d'annulation.
    /// </summary>
    /// <param name="folder">Dossier a parcourir.</param>
    /// <param name="query">Criteres de selection.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les messages correspondants, au fil de l'eau.</returns>
    IAsyncEnumerable<MessageRef> ListAsync(FolderRef folder, MessageQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Telecharge le MIME brut. L'appelant est proprietaire du flux et doit le liberer.
    /// Toute implementation ecrit dans un <c>using</c> : les flux non fermes sont la
    /// cause racine historique des verrous fichiers en production.
    /// </summary>
    /// <param name="message">Message a telecharger.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Un flux positionne au debut du MIME, a liberer par l'appelant.</returns>
    Task<Stream> OpenRawMessageAsync(MessageRef message, CancellationToken cancellationToken);

    /// <summary>Applique la disposition apres traitement reussi. Idempotent.</summary>
    /// <param name="message">Message concerne.</param>
    /// <param name="disposition">Action a appliquer.</param>
    /// <param name="target">Dossier cible, requis pour un deplacement.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand la disposition est appliquee.</returns>
    Task ApplyDispositionAsync(
        MessageRef message,
        MessageDisposition disposition,
        FolderRef? target,
        CancellationToken cancellationToken);
}

/// <summary>Fabrique de sources, resolue par type declare en configuration ("imap", "graph").</summary>
public interface IMailSourceFactory
{
    /// <summary>Protocole gere, tel qu'il apparait en configuration.</summary>
    string ProtocolType { get; }

    /// <summary>Instancie une source a partir de sa section de configuration.</summary>
    /// <param name="services">Fournisseur de services du socle.</param>
    /// <param name="configuration">Section de configuration de la source.</param>
    /// <returns>La source prete a l'emploi.</returns>
    IMailSource Create(IServiceProvider services, IConfiguration configuration);
}
