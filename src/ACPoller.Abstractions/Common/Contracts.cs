namespace ACPoller.Abstractions.Common;

/// <summary>
/// Version du contrat d'extension. Incrementee a chaque rupture binaire.
/// Le loader compare cette valeur a celle declaree par le plugin et refuse
/// le chargement en cas d'ecart, au lieu de planter a la premiere invocation.
/// </summary>
public static class ContractVersion
{
    /// <summary>Version courante du contrat, declaree par les plugins compiles contre ce socle.</summary>
    public const int Current = 1;

    /// <summary>Versions encore acceptees par le core.</summary>
    public static readonly int[] Supported = [1];
}

/// <summary>
/// Nature d'un echec. Remplace l'interpretation au cas par cas des codes HTTP
/// et des exceptions : c'est le producteur de l'erreur qui qualifie, pas le retry.
/// </summary>
public enum FailureKind
{
    /// <summary>Pas d'echec.</summary>
    None = 0,

    /// <summary>Echec temporaire : reseau, 429, 5xx, verrou. A rejouer avec backoff.</summary>
    Transient = 1,

    /// <summary>Echec definitif : 400, 401, 403, 404, fichier invalide. Ne jamais rejouer.</summary>
    Permanent = 2,

    /// <summary>Cas non pris en charge (format inconnu). Ni erreur, ni succes.</summary>
    Unsupported = 3,
}

/// <summary>Detail d'un echec, transportable sans exception.</summary>
/// <param name="Kind">Qualification de l'echec, qui decide du rejeu.</param>
/// <param name="Reason">Message lisible, destine aux journaux et a l'UI.</param>
/// <param name="Code">Code technique d'origine (statut HTTP, code fournisseur), optionnel.</param>
/// <param name="Exception">Exception d'origine, conservee pour le diagnostic.</param>
public sealed record Failure(FailureKind Kind, string Reason, string? Code = null, Exception? Exception = null)
{
    /// <summary>Vrai si l'operation merite une nouvelle tentative.</summary>
    public bool IsTransient => Kind == FailureKind.Transient;

    /// <summary>Construit un echec transitoire, donc rejouable.</summary>
    /// <param name="reason">Message lisible.</param>
    /// <param name="code">Code technique d'origine, optionnel.</param>
    /// <param name="exception">Exception d'origine, optionnelle.</param>
    /// <returns>Un echec qualifie transitoire.</returns>
    public static Failure Transient(string reason, string? code = null, Exception? exception = null)
        => new(FailureKind.Transient, reason, code, exception);

    /// <summary>Construit un echec permanent, a ne jamais rejouer.</summary>
    /// <param name="reason">Message lisible.</param>
    /// <param name="code">Code technique d'origine, optionnel.</param>
    /// <param name="exception">Exception d'origine, optionnelle.</param>
    /// <returns>Un echec qualifie permanent.</returns>
    public static Failure Permanent(string reason, string? code = null, Exception? exception = null)
        => new(FailureKind.Permanent, reason, code, exception);

    /// <summary>Construit un cas non pris en charge, qui n'est ni une erreur ni un succes.</summary>
    /// <param name="reason">Message lisible.</param>
    /// <returns>Un echec qualifie non pris en charge.</returns>
    public static Failure Unsupported(string reason)
        => new(FailureKind.Unsupported, reason);
}

/// <summary>
/// Exception de base du socle. Porte toujours la qualification de l'echec :
/// aucun consommateur ne doit avoir a deviner si l'operation est rejouable.
/// Regle de la solution : toujours <c>throw;</c>, jamais <c>throw ex;</c>.
/// </summary>
public class AcPollerException : Exception
{
    /// <summary>Construit l'exception a partir d'un echec deja qualifie.</summary>
    /// <param name="failure">Echec porte par l'exception.</param>
    public AcPollerException(Failure failure)
        : base(failure.Reason, failure.Exception) => Failure = failure;

    /// <summary>Construit l'exception en qualifiant l'echec sur place.</summary>
    /// <param name="kind">Nature de l'echec.</param>
    /// <param name="reason">Message lisible.</param>
    /// <param name="code">Code technique d'origine, optionnel.</param>
    /// <param name="inner">Exception d'origine, optionnelle.</param>
    public AcPollerException(FailureKind kind, string reason, string? code = null, Exception? inner = null)
        : base(reason, inner) => Failure = new Failure(kind, reason, code, inner);

    /// <summary>Echec qualifie porte par cette exception.</summary>
    public Failure Failure { get; }

    /// <summary>Vrai si l'operation merite une nouvelle tentative.</summary>
    public bool IsTransient => Failure.IsTransient;
}

/// <summary>Resultat d'un test de connectivite declenche depuis l'UI de parametrage.</summary>
/// <param name="Success">Vrai si la connexion a abouti.</param>
/// <param name="Message">Message affichable tel quel dans l'UI.</param>
/// <param name="Elapsed">Duree du test, utile pour reperer une latence anormale.</param>
/// <param name="Details">Informations complementaires (version serveur, dossiers vus), optionnelles.</param>
public sealed record ConnectionCheck(
    bool Success,
    string Message,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, string>? Details = null)
{
    /// <summary>Construit un resultat de test reussi.</summary>
    /// <param name="message">Message affichable.</param>
    /// <param name="elapsed">Duree du test.</param>
    /// <returns>Un resultat marque comme reussi.</returns>
    public static ConnectionCheck Ok(string message, TimeSpan elapsed) => new(true, message, elapsed);

    /// <summary>Construit un resultat de test echoue.</summary>
    /// <param name="message">Message affichable expliquant l'echec.</param>
    /// <param name="elapsed">Duree ecoulee avant l'echec.</param>
    /// <returns>Un resultat marque comme echoue.</returns>
    public static ConnectionCheck Ko(string message, TimeSpan elapsed) => new(false, message, elapsed);
}

/// <summary>Composant testable depuis l'UI sans demarrer de collecte.</summary>
public interface IConnectionTestable
{
    /// <summary>Verifie la connectivite et les droits, sans traiter de message.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le resultat du test, affichable tel quel.</returns>
    Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken);
}
