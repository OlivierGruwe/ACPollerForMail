namespace ACPoller.Ui;

/// <summary>
/// Textes d'aide affiches en infobulle.
/// </summary>
/// <remarks>
/// Centralises ici plutot qu'ecrits dans le XAML pour deux raisons : ils sont
/// relus d'un coup quand on veut verifier la coherence du vocabulaire, et une
/// meme explication sert a plusieurs ecrans sans etre recopiee.
///
/// Regle d'ecriture : dire ce qui se passe si le reglage est mal renseigne,
/// pas repeter le libelle du champ. Une infobulle qui dit "nom de la boite"
/// sous un champ intitule "Boite" n'apporte rien.
/// </remarks>
public static class Help
{
    // ---- General -----------------------------------------------------------

    /// <summary>Aide du nom de configuration.</summary>
    public const string Name =
        "Identifiant unique, utilise comme cle de correlation dans les journaux et "
        + "comme nom du repertoire de travail. Le changer revient a creer une nouvelle "
        + "configuration : l'ancienne subsistera tant qu'elle n'est pas supprimee.";

    /// <summary>Aide de l'etat actif.</summary>
    public const string Enabled =
        "Une configuration inactive n'a pas de worker : aucun message n'est collecte, "
        + "mais le parametrage est conserve. C'est la facon propre de suspendre un flux.";

    /// <summary>Aide du gabarit.</summary>
    public const string Template =
        "Nom d'un gabarit declare dans Poller:Templates. La configuration en herite "
        + "toutes les valeurs et ne surcharge que ses ecarts. Sur un parc de dizaines "
        + "de boites partageant le meme tenant, une configuration se resume alors a "
        + "trois lignes.";

    /// <summary>Aide de la periode de collecte.</summary>
    public const string Interval =
        "Delai entre la fin d'un cycle et le debut du suivant. Une valeur trop courte "
        + "sur un grand nombre de boites multiplie les appels sans rien gagner : la "
        + "collecte est deja bornee par la concurrence globale du service.";

    /// <summary>Aide du decalage au demarrage.</summary>
    public const string Jitter =
        "Attente aleatoire avant le premier cycle. Sans elle, toutes les boites "
        + "frappent le serveur a la meme seconde au demarrage du service, ce qui "
        + "declenche le throttling avant meme le premier message.";

    /// <summary>Aide du filtre non lus.</summary>
    public const string UnreadOnly =
        "Filtre applique cote serveur. Attention : si la disposition ne marque pas "
        + "les messages comme lus, ce filtre ne suffit pas a eviter le retraitement. "
        + "C'est le journal des messages traites qui garantit l'idempotence.";

    /// <summary>Aide du plafond par cycle.</summary>
    public const string MaxCount =
        "Borne le travail d'un cycle. Une boite laissee sans traitement pendant des "
        + "semaines ne doit pas produire un cycle de plusieurs heures pendant lequel "
        + "les autres boites attendent.";

    /// <summary>Aide de la date plancher.</summary>
    public const string NotBefore =
        "Aucun message anterieur a cette date ne sera jamais traite. A renseigner a la "
        + "mise en production quand l'historique n'est pas repris : sans elle, le "
        + "premier demarrage traite tout ce qui dort dans la boite. Contrairement a un "
        + "rattrapage relatif, cette date ne glisse pas au redemarrage.";

    // ---- Protocole ---------------------------------------------------------

    /// <summary>Aide du protocole.</summary>
    public const string Protocol =
        "graph pour Microsoft 365, imap pour une messagerie classique, folder pour "
        + "lire des fichiers .eml sur disque. Le mode folder sert au rejeu d'un "
        + "message litigieux et a la recette, sans boite ni reseau.";

    /// <summary>Aide de l'adresse de boite.</summary>
    public const string Mailbox =
        "Sert aussi de cle au journal des messages traites. La modifier repart d'un "
        + "journal vide, donc retraite ce qui est present dans la boite.";

    /// <summary>Aide du secret client.</summary>
    public const string ClientSecret =
        "Laisser vide conserve la valeur actuelle : le secret n'est jamais renvoye a "
        + "l'interface. Saisir une valeur la remplace, et elle est chiffree sur le "
        + "serveur des l'enregistrement.";

    /// <summary>Aide de l'empreinte de certificat.</summary>
    public const string Certificate =
        "Empreinte d'un certificat present dans le magasin machine, dont le compte de "
        + "service doit pouvoir lire la cle privee. A preferer au secret client, qui "
        + "expire toujours un vendredi soir.";

    /// <summary>Aide du dossier surveille.</summary>
    public const string Folder =
        "Designe par son nom canonique et non par son libelle : sur une boite en "
        + "francais, le libelle est 'Boite de reception' et toute recherche par nom "
        + "echoue.";

    /// <summary>Aide de la disposition.</summary>
    public const string Disposition =
        "Applique APRES export confirme. None laisse le message intact, ce qui permet "
        + "de rejouer autant qu'on veut pendant une recette. En production, MarkAsRead "
        + "ou Move evitent de relister indefiniment les memes messages.";

    // ---- Conversion --------------------------------------------------------

    /// <summary>Aide du delai de conversion.</summary>
    public const string Timeout =
        "Depasse, le convertisseur est TUE et non attendu. Un LibreOffice bloque sur "
        + "un document corrompu ne se debloque jamais : sans ce delai, un seul "
        + "document fige un slot de conversion definitivement.";

    /// <summary>Aide de la politique d'echec.</summary>
    public const string OnFailure =
        "Substitute produit une page portant le nom du fichier et le motif : la GED "
        + "voit qu'il manque quelque chose. Skip omet la piece silencieusement. "
        + "RejectMessage rejette tout le message, aucune sortie partielle.";

    /// <summary>Aide du seuil d'image incorporee.</summary>
    public const string MinInlineImage =
        "En dessous de ce seuil, une image referencee par le corps HTML est ecartee. "
        + "Sans ce filtre, chaque mail produit trois pages de logos de signature.";

    /// <summary>Aide des bornes d'extraction.</summary>
    public const string ExtractionLimits =
        "Ces bornes protegent d'une archive forgee : une archive recursive de quelques "
        + "kilo-octets peut saturer un disque. Les augmenter sans raison expose le "
        + "service, puisque les fichiers traites arrivent par mail, donc de n'importe qui.";

    // ---- Sortie ------------------------------------------------------------

    /// <summary>Aide du mode PDF.</summary>
    public const string PdfMode =
        "Merged produit un seul PDF par message, corps puis pieces, avec un signet par "
        + "piece. PerAttachment produit un PDF par piece. Both produit les deux, pour "
        + "les GED qui indexent l'unifie et archivent le detail.";

    /// <summary>Aide du format de metadonnees.</summary>
    public const string MetadataFormat =
        "xslt applique une feuille de transformation au XML canonique : c'est ce qui "
        + "permet de produire la structure exacte attendue par une GED, y compris du "
        + "texte plat, sans livraison de code.";

    /// <summary>Aide du gabarit de nommage.</summary>
    public const string Naming =
        "Jetons disponibles : {date} {time} {received} {guid} {mailbox} {config} "
        + "{subject} {from} {index} {name} {ext} {node}. Un format peut suivre deux "
        + "points, par exemple {received:yyyyMMdd}. En mode PerAttachment, {index} est "
        + "obligatoire sous peine d'ecrasement entre pieces.";

    /// <summary>Aide de l'ecriture atomique.</summary>
    public const string AtomicWrite =
        "Le fichier est ecrit sous une extension temporaire puis renomme. Sans cela, "
        + "une GED qui scrute le repertoire peut ramasser un PDF en cours d'ecriture, "
        + "et l'erreur est indetectable de son cote.";

    /// <summary>Aide du mode tout ou rien.</summary>
    public const string AllOrNothing =
        "Si une cible echoue, les depots deja faits sur les autres sont annules. Une "
        + "GED qui recoit un message qu'une seconde n'a pas recu produit un ecart de "
        + "reconciliation que personne ne detecte avant l'audit.";

    /// <summary>Aide du fichier temoin.</summary>
    public const string Sentinel =
        "Ecrit en dernier, une fois tous les autres fichiers deposes. Il indique a la "
        + "GED que le lot est complet. Sans lui, elle peut prendre le PDF sans son "
        + "fichier d'information, ou l'inverse.";

    // ---- Cibles ------------------------------------------------------------

    /// <summary>Aide du type de cible.</summary>
    public const string TargetType =
        "fs pour un repertoire local ou UNC, ftp pour un serveur FTP ou FTPS, s3 pour "
        + "un stockage objet, plugin pour une DLL d'export cliente.";

    /// <summary>Aide du nom de cible.</summary>
    public const string TargetName =
        "Repris dans les journaux et les messages d'erreur. Sans nom distinct, un "
        + "echec sur l'une des trois cibles d'un flux est illisible.";

    /// <summary>Aide de la validation de certificat FTP.</summary>
    public const string AcceptAnyCertificate =
        "Supprime toute protection contre l'interception : un tiers peut se placer "
        + "entre le service et le serveur sans etre detecte. A n'activer que sur un "
        + "certificat auto-signe connu, et a documenter. Le service le rappelle a "
        + "chaque session dans son journal.";

    /// <summary>Aide du point d'acces S3 personnalise.</summary>
    public const string S3ServiceUrl =
        "Pour MinIO, Garage, Ceph ou tout stockage compatible. Renseigne, le style "
        + "chemin et le mode de somme de controle compatible sont actives "
        + "automatiquement : sans eux, ces implementations refusent les depots avec "
        + "une erreur qui ne designe pas la cause.";

    /// <summary>Aide du chiffrement au repos S3.</summary>
    public const string S3Encryption =
        "AES256 sur AWS. Laisser VIDE sur MinIO ou Garage si le chiffrement serveur "
        + "n'y est pas configure, sinon chaque depot est refuse.";

    // ---- Champs ------------------------------------------------------------

    /// <summary>Aide de l'origine d'un champ.</summary>
    public const string FieldSource =
        "Fixed pour une constante, Token pour un gabarit a jetons, Header pour un "
        + "en-tete MIME, Property pour une valeur deposee par un traitement metier, "
        + "Environment pour une variable du serveur.";

    /// <summary>Aide de la longueur fixe.</summary>
    public const string FieldFixedLength =
        "Pour les GED qui lisent du positionnel. Une valeur trop longue est tronquee : "
        + "deborder decalerait toutes les colonnes suivantes, ce qui est pire que "
        + "perdre la fin d'un libelle.";

    /// <summary>Aide des traductions de valeur.</summary>
    public const string FieldValues =
        "Appliquees apres resolution : convertit un libelle en code attendu par la "
        + "GED. Si la valeur lue n'est pas dans la table, elle passe inchangee.";

    /// <summary>Aide de l'ordre des champs.</summary>
    public const string FieldOrder =
        "L'ordre fait foi : c'est celui des colonnes du CSV. Le modifier change le "
        + "format de sortie et peut casser une integration cliente en place.";

    /// <summary>Aide du rattrapage initial.</summary>
    public const string InitialLookback =
        "Ne s'applique qu'au tout premier cycle, quand le service n'a encore rien "
        + "traite pour cette boite. Une valeur trop faible fait croire que rien "
        + "n'arrive alors que le service fonctionne, et c'est le diagnostic le plus "
        + "penible parce que rien ne le signale.";

    /// <summary>Aide du filtre sur pieces jointes.</summary>
    public const string RequireAttachments =
        "Filtre applique cote serveur. Attention : un message dont la seule piece "
        + "est une image de signature compte comme ayant une piece jointe.";

    /// <summary>Aide du respect du delai Graph.</summary>
    public const string HonorRetryAfter =
        "Graph indique combien de temps attendre apres une limitation. L'ignorer "
        + "aggrave la situation : le service repart aussitot, reprend une limitation, "
        + "et le quota se reconstitue de plus en plus lentement. A ne desactiver qu'en "
        + "diagnostic.";

    /// <summary>Aide des extensions ecartees.</summary>
    public const string ExcludedExtensions =
        "Comparees a l'extension du fichier, point compris. Une piece ecartee "
        + "n'apparait pas dans le PDF, mais reste mentionnee dans le fichier "
        + "d'information avec le statut Excluded.";
}
