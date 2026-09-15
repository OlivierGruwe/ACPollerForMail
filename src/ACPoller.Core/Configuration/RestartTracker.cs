using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Configuration;

/// <summary>Un reglage modifie qui ne prendra effet qu'au redemarrage.</summary>
/// <param name="Path">Chemin du reglage, ex. Poller:MaxGlobalConcurrency.</param>
/// <param name="Reason">Pourquoi un redemarrage est necessaire.</param>
/// <param name="ChangedUtc">Quand la modification a eu lieu.</param>
public sealed record PendingRestartItem(string Path, string Reason, DateTimeOffset ChangedUtc);

/// <summary>
/// Suit les modifications de configuration qui exigent un redemarrage complet.
/// </summary>
/// <remarks>
/// Tout n'est pas rechargeable a chaud, et la difference n'est pas devinable
/// depuis l'ecran. Un exploitant qui augmente la concurrence globale et ne voit
/// rien changer conclura que le produit ne fonctionne pas, alors qu'il lui
/// manque une information que le service, lui, possede.
///
/// La liste est volontairement EXPLICITE plutot que deduite : ajouter un
/// reglage non rechargeable sans l'inscrire ici produirait un silence, ce qui
/// est exactement le defaut qu'on corrige. Le test qui verifie que chaque
/// chemin declare existe reellement dans PollerOptions evite la derive.
/// </remarks>
public sealed class RestartTracker(ILogger<RestartTracker> logger)
{
    /// <summary>
    /// Reglages non rechargeables, avec la raison. Le prefixe termine par deux
    /// points designe une section entiere.
    /// </summary>
    private static readonly (string Path, string Reason)[] RequiresRestart =
    [
        ("Poller:WorkDirectory",
            "Le repertoire de travail est resolu au demarrage et les unites en cours y vivent."),

        ("Poller:MaxGlobalConcurrency",
            "Le semaphore global est construit une fois, sa capacite ne se change pas a chaud."),

        ("Poller:PluginDirectory",
            "Les plugins sont charges dans un contexte d'assemblage cree au demarrage."),

        ("Poller:ProcessedRetentionDays",
            "La base d'idempotence est ouverte au demarrage."),

        ("ControlApi:",
            "Le serveur d'ecoute est demarre avec l'hote : port, jeton et activation."),

        ("Conversion:LibreOffice:",
            "Le pool de profils LibreOffice est dimensionne au demarrage."),

        ("Conversion:Fonts:",
            "Le resolveur de polices est installe une fois, avant tout traitement."),

        ("Metrics:",
            "Le fournisseur de metriques est choisi au demarrage."),

        ("Maintenance:",
            "Le minuteur de maintenance est arme au demarrage."),

        ("Logging:",
            "Les niveaux de journalisation sont fixes a la construction de l'hote."),
    ];

    private readonly ConcurrentDictionary<string, PendingRestartItem> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si au moins une modification attend un redemarrage.</summary>
    public bool IsRestartRequired => !_pending.IsEmpty;

    /// <summary>Modifications en attente, la plus ancienne en tete.</summary>
    public IReadOnlyList<PendingRestartItem> Pending =>
        [.. _pending.Values.OrderBy(p => p.ChangedUtc)];

    /// <summary>
    /// Determine si un chemin de reglage exige un redemarrage.
    /// </summary>
    /// <param name="path">Chemin du reglage.</param>
    /// <returns>La raison, ou null si le reglage est rechargeable a chaud.</returns>
    public static string? GetRestartReason(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var (prefix, reason) in RequiresRestart)
        {
            var matches = prefix.EndsWith(':')
                ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                : string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>Enregistre une modification qui attend un redemarrage.</summary>
    /// <param name="path">Chemin du reglage modifie.</param>
    public void Mark(string path)
    {
        var reason = GetRestartReason(path);

        if (reason is null)
        {
            return;
        }

        _pending[path] = new PendingRestartItem(path, reason, DateTimeOffset.UtcNow);

        logger.LogWarning(
            "Reglage {Path} modifie : un redemarrage du service est necessaire pour qu'il prenne effet.",
            path);
    }

    /// <summary>
    /// Efface les modifications en attente. Appele au demarrage du service.
    /// </summary>
    /// <remarks>
    /// Le suivi est en memoire et non persiste : apres redemarrage, les
    /// reglages sont par definition appliques. Persister l'etat obligerait a le
    /// vider correctement, avec le risque d'un avertissement qui ne part jamais
    /// et qu'on finit par ignorer.
    /// </remarks>
    public void Clear() => _pending.Clear();
}
