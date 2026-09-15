using System.ComponentModel.DataAnnotations;

namespace ACPoller.Core.Configuration;

/// <summary>Origine de la valeur d'un champ personnalise.</summary>
public enum FieldSource
{
    /// <summary>Valeur litterale, identique pour tous les messages.</summary>
    Fixed = 0,

    /// <summary>
    /// Gabarit a jetons, resolu par le meme moteur que le nommage des fichiers :
    /// {date}, {received}, {subject}, {from}, {mailbox}, {config}, {guid}.
    /// </summary>
    Token = 1,

    /// <summary>En-tete MIME du message, designe par son nom.</summary>
    Header = 2,

    /// <summary>
    /// Propriete deposee par un traitement metier dans le sac de proprietes.
    /// C'est le canal par lequel un plugin fait remonter un code fournisseur
    /// ou un code societe extrait du sujet.
    /// </summary>
    Property = 3,

    /// <summary>Variable d'environnement du serveur.</summary>
    Environment = 4,
}

/// <summary>
/// Un champ ajoute au fichier d'information.
/// </summary>
/// <remarks>
/// Ce mecanisme existe parce qu'aucun format fige ne convient a toutes les GED :
/// l'une veut un code societe en dur, l'autre la date du jour dans son propre
/// format, une troisieme un en-tete X- pose par son relais. Les figer dans le
/// code imposerait une livraison par client.
///
/// La resolution suit toujours le meme ordre : valeur d'origine, puis
/// traduction, puis valeur par defaut si le resultat est vide.
/// </remarks>
public sealed class MetadataFieldOptions
{
    /// <summary>Nom du champ tel qu'il apparait dans le fichier de sortie.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Origine de la valeur.</summary>
    public FieldSource Source { get; set; } = FieldSource.Token;

    /// <summary>
    /// Valeur, dont l'interpretation depend de la source : le texte litteral
    /// pour Fixed, le gabarit pour Token, le nom de l'en-tete pour Header, la
    /// cle pour Property, le nom de la variable pour Environment.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Format applique a une valeur reconnue comme date. Ignore sinon.
    /// Exemple : yyyy-MM-dd, ou yyyyMMdd pour un import a plat.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>Valeur retenue quand la resolution ne donne rien.</summary>
    public string? Default { get; set; }

    /// <summary>
    /// Table de traduction appliquee a la valeur resolue. Sert a convertir un
    /// libelle en code attendu par la GED, par exemple un nom de societe en
    /// code societe.
    /// </summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Longueur fixe imposee, avec troncature ou remplissage. Zero pour laisser
    /// la valeur telle quelle. Utile pour les GED qui lisent du largeur fixe.
    /// </summary>
    public int FixedLength { get; set; }

    /// <summary>Caractere de remplissage quand FixedLength est renseigne.</summary>
    public char PaddingChar { get; set; } = ' ';

    /// <summary>Remplir a gauche plutot qu'a droite. Vrai pour les valeurs numeriques.</summary>
    public bool PadLeft { get; set; }
}
