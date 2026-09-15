using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;

namespace ACPoller.Abstractions.Processing;

/// <summary>Point d'insertion d'un traitement metier dans le pipeline.</summary>
public enum ProcessingStage
{
    /// <summary>Apres parsing du MIME, avant extraction des conteneurs. Filtrage precoce.</summary>
    AfterParse = 0,

    /// <summary>Apres extraction recursive, avant conversion. Selection des pieces a convertir.</summary>
    AfterExtract = 1,

    /// <summary>Apres conversion PDF, avant assemblage. Enrichissement des metadonnees.</summary>
    AfterConvert = 2,

    /// <summary>Avant export. Dernier mot sur la nomenclature et le routage.</summary>
    BeforeExport = 3,

    /// <summary>Apres export reussi. Acquittements, notifications de rejet.</summary>
    AfterExport = 4,
}

/// <summary>Decision rendue par un traitement metier.</summary>
public enum ProcessingDecision
{
    /// <summary>Poursuivre le pipeline.</summary>
    Continue = 0,

    /// <summary>Abandonner ce message sans erreur : il ne concerne pas ce flux.</summary>
    Skip = 1,

    /// <summary>Rejeter avec un code metier. Declenche la notification de rejet.</summary>
    Reject = 2,
}

/// <summary>Issue d'un traitement metier.</summary>
public sealed record ProcessingOutcome
{
    /// <summary>Decision rendue.</summary>
    public required ProcessingDecision Decision { get; init; }

    /// <summary>Code de rejet metier, renseigne en cas de rejet.</summary>
    public string? RejectionCode { get; init; }

    /// <summary>Motif lisible, repris dans les journaux et la notification.</summary>
    public string? Reason { get; init; }

    /// <summary>Echec technique associe, quand la decision decoule d'une erreur.</summary>
    public Failure? Failure { get; init; }

    /// <summary>Construit une decision de poursuite.</summary>
    /// <returns>Une issue demandant la poursuite du pipeline.</returns>
    public static ProcessingOutcome Continue() => new() { Decision = ProcessingDecision.Continue };

    /// <summary>Construit une decision d'abandon sans erreur.</summary>
    /// <param name="reason">Motif de l'abandon.</param>
    /// <returns>Une issue demandant l'abandon du message.</returns>
    public static ProcessingOutcome Skip(string reason) =>
        new() { Decision = ProcessingDecision.Skip, Reason = reason };

    /// <summary>Construit une decision de rejet metier.</summary>
    /// <param name="code">Code de rejet.</param>
    /// <param name="reason">Motif du rejet.</param>
    /// <returns>Une issue demandant le rejet du message.</returns>
    public static ProcessingOutcome Reject(string code, string reason) =>
        new() { Decision = ProcessingDecision.Reject, RejectionCode = code, Reason = reason };
}

/// <summary>
/// Traitement metier client. Remplace l'invocation par reflexion de l'ancien
/// <c>applyAssembly</c> : la resolution se fait par interface, plus par signature,
/// donc une erreur de contrat se voit a la compilation et non a l'execution.
/// </summary>
public interface ICaptureProcessor
{
    /// <summary>Nom du processeur, tel qu'il est declare en configuration.</summary>
    string Name { get; }

    /// <summary>Etapes auxquelles ce processeur doit etre appele.</summary>
    IReadOnlyList<ProcessingStage> Stages { get; }

    /// <summary>Ordre d'execution entre processeurs d'une meme etape, croissant.</summary>
    int Order { get; }

    /// <summary>Execute le traitement metier pour l'etape courante.</summary>
    /// <param name="context">Contexte du message, modifiable.</param>
    /// <param name="stage">Etape en cours.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>La decision a appliquer.</returns>
    Task<ProcessingOutcome> ProcessAsync(
        CaptureContext context,
        ProcessingStage stage,
        CancellationToken cancellationToken);
}

/// <summary>Referentiel de donnees metier expose aux processeurs (base client, tables de correspondance).</summary>
public interface IReferentialProvider
{
    /// <summary>Recherche une entree du referentiel.</summary>
    /// <param name="table">Table logique interrogee.</param>
    /// <param name="key">Cle de recherche.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les colonnes de l'entree, ou null si elle est absente.</returns>
    Task<IReadOnlyDictionary<string, string>?> LookupAsync(
        string table,
        string key,
        CancellationToken cancellationToken);

    /// <summary>Invalide le cache. Appelee par l'UI, pas a chaque cycle de collecte.</summary>
    /// <param name="table">Table a invalider, ou null pour tout invalider.</param>
    /// <returns>Une tache achevee quand le cache est vide.</returns>
    Task InvalidateAsync(string? table = null);
}
